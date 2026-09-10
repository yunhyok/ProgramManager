using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ProgramManager.Core;

#if NET8_0_OR_GREATER
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "--artifacts") { await Artifacts(args[1], args[2]); return 0; }
            if (args.Length == 3 && args[0] == "--serve") { await Serve(args[1], int.Parse(args[2])); return 0; }
            if (args.Length == 2 && args[0] == "--client") { await Client(args[1]); return 0; }
            var root = Path.Combine(Path.GetTempPath(), "ProgramManagerChecks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { await Checks(root); await GitHubTransportChecks.RunAsync(root); await GitHubChecks.RunAsync(root); }
            finally
            {
                if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test path");
                Directory.Delete(root, true);
            }
            Console.WriteLine("PASS: catalog, numeric/platform history, immutable publish, validation, identity, TLS pairing, download/hash/cancellation cleanup");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Artifacts(string modern, string legacy)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProgramManagerArtifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CatalogStore(Path.Combine(root, "repository"));
            await store.PublishAsync("real-installer", "Real installer validation", "Production-sized EXE packages", "1.0", "Modern", modern, "win10-x64");
            await store.PublishAsync("real-installer", "Real installer validation", "Production-sized EXE packages", "1.0", "Legacy", legacy, "win7");
            using var identity = HostIdentity.LoadOrCreate(Path.Combine(root, "identity"));
            var info = identity.CreatePairing("127.0.0.1", Port());
            using var server = new CatalogServer(store, identity);
            await server.StartAsync(info.Port);
            using var client = new CatalogClient(info);
            var app = (await client.FetchCatalogAsync()).Apps.Single();
            foreach (var release in app.Releases)
            {
                var path = await client.DownloadAsync(app, release, Path.Combine(root, "downloads"));
                var source = release.Platform == "win7" ? legacy : modern;
                using var sha = SHA256.Create();
                using var original = File.OpenRead(source);
                using var received = File.OpenRead(path);
                Assert(sha.ComputeHash(original).SequenceEqual(sha.ComputeHash(received)), "real artifact hash " + release.Platform);
                Console.WriteLine($"PASS: {release.Platform} {release.Size:N0} bytes {Path.GetFileName(source)}");
            }
            await server.StopAsync();
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test path");
            Directory.Delete(root, true);
        }
    }

    private static async Task Checks(string root)
    {
        var original = Path.Combine(root, "Setup.exe");
        var win7 = Path.Combine(root, "Setup-Win7.msi");
        File.WriteAllBytes(original, Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray());
        File.WriteAllBytes(win7, Enumerable.Range(0, 70000).Select(i => (byte)(i % 241)).ToArray());
        var store = new CatalogStore(Path.Combine(root, "host"));
        Assert(store.Read().Apps.Count == 0, "new empty catalog");
        await store.PublishAsync("sample-app", "Example", "description", "1.9", "first", original);
        await store.PublishAsync("sample-app", "Example", "description", "1.10", "latest", original);
        var catalog = await store.PublishAsync("sample-app", "Example", "description", "1.10.0", "Windows 7", win7, "win7");
        Assert(catalog.Apps.Single().Releases.Count == 3, "both platforms same version");
        Assert(catalog.Apps.Single().Latest!.Version == "1.10", "numeric version ordering");
        var modernPath = store.GetPackagePath("sample-app", "1.10");
        var legacyPath = store.GetPackagePath("sample-app", "1.10.0.0", "win7");
        Assert(modernPath != legacyPath && File.ReadAllBytes(legacyPath).SequenceEqual(File.ReadAllBytes(win7)), "platform package isolation");
        var originalCatalog = File.ReadAllText(Path.Combine(root, "host", "catalog.json"));
        await Reject(() => store.PublishAsync("sample-app", "changed", "", "1.10.0.0", "duplicate", win7), "normalized duplicate");
        Assert(originalCatalog == File.ReadAllText(Path.Combine(root, "host", "catalog.json")), "duplicate leaves catalog untouched");
        await Reject(() => store.PublishAsync("../escape", "Example", "", "2.0", "", original), "ID traversal");
        await Reject(() => store.PublishAsync("sample-app", "Example", "", "2.0", "", original, "../win7"), "platform traversal");
        await Reject(() => store.PublishAsync("sample-app", "Example", "", "2.0-beta", "", original), "nonnumeric version");
        await Reject(() => Task.FromResult(store.GetPackagePath("sample-app", "../../Setup")), "version traversal");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject(() => store.PublishAsync("sample-app", "Example", "", "2.0", "", original, token: cancelled.Token), "cancelled publication");
        }
        Assert(!Directory.GetFiles(Path.Combine(root, "host"), "*.part", SearchOption.AllDirectories).Any(), "publish partial cleanup");
        File.WriteAllText(Path.Combine(root, "host", "catalog.json"), "{broken");
        await Reject(() => Task.FromResult(store.Read()), "corrupt catalog fails closed");
        File.WriteAllText(Path.Combine(root, "host", "catalog.json"), originalCatalog);
        var hostile = store.Read(); hostile.Apps[0].Releases.Add(hostile.Apps[0].Releases[0]);
        await Reject(() => Task.FromResult(CatalogRules.Validate(hostile)), "duplicate hostile catalog");
        hostile = store.Read(); hostile.Apps[0].Releases[0].FileName = "../setup.exe";
        await Reject(() => Task.FromResult(CatalogRules.Validate(hostile)), "hostile filename");
        hostile = store.Read(); hostile.Apps[0].Releases = null!;
        await Reject(() => Task.FromResult(CatalogRules.Validate(hostile)), "null release list");
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(root, "identity"));
        using (var reloaded = HostIdentity.LoadOrCreate(Path.Combine(root, "identity")))
            Assert(identity.Fingerprint == reloaded.Fingerprint && identity.Token == reloaded.Token, "identity persistence");
        Assert(!File.ReadAllText(Path.Combine(root, "identity", "host-identity.json")).Contains(identity.Token), "protected token storage");
        var secret = Secrets.Protect("test-secret"); Assert(Secrets.Unprotect(secret) == "test-secret", "DPAPI round trip");
        var info = identity.CreatePairing("127.0.0.1", Port());
        Assert(PairingInfo.Parse(info.Export()).Fingerprint == identity.Fingerprint, "pairing roundtrip");
        var unsafeInfo = identity.CreatePairing("localhost"); unsafeInfo.Host = "localhost/path?token=x";
        await Reject(() => Task.FromResult(unsafeInfo.Export()), "host injection");
        using var server = new CatalogServer(store, identity);
        await server.StartAsync(info.Port);
        using var client = new CatalogClient(info);
        var fetched = await client.FetchCatalogAsync();
        Assert(fetched.Apps[0].Releases.Count == 3, "TLS catalog read");
        var app = fetched.Apps[0]; var release = app.Releases.Single(r => r.Version == "1.10" && r.Platform == "win10-x64");
        var destination = Path.Combine(root, "downloads");
        var downloaded = await client.DownloadAsync(app, release, destination);
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(File.ReadAllBytes(original)), "verified download");
        var oldRelease = app.Releases.Single(r => r.Platform == "win7");
        var oldDownloaded = await client.DownloadAsync(app, oldRelease, destination);
        Assert(File.ReadAllBytes(oldDownloaded).SequenceEqual(File.ReadAllBytes(win7)), "Windows 7 package download");
        var wrongToken = PairingInfo.Parse(info.Export()); wrongToken.Token = new string('0', 64);
        using (var bad = new CatalogClient(wrongToken)) await Reject(() => bad.FetchCatalogAsync(), "wrong token");
        var wrongPin = PairingInfo.Parse(info.Export()); wrongPin.Fingerprint = new string('0', 64);
        using (var bad = new CatalogClient(wrongPin)) await Reject(() => bad.FetchCatalogAsync(), "wrong certificate pin");
        var tampered = File.ReadAllBytes(modernPath); tampered[0] ^= 0xff; File.WriteAllBytes(modernPath, tampered);
        release.Sha256 = Hash(tampered); // Returned object must not mutate the pinned private snapshot.
        await Reject(() => client.DownloadAsync(app, release, destination), "hash tamper rejected despite mutated returned object");
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(File.ReadAllBytes(original)), "failed update preserves previous verified file");
        Assert(!Directory.GetFiles(destination, "*.part").Any(), "hash failure partial cleanup");
        File.WriteAllBytes(modernPath, File.ReadAllBytes(original));
        using (var cancelled = new CancellationTokenSource())
        {
            var progress = new CallbackProgress(_ => cancelled.Cancel());
            await Reject(() => client.DownloadAsync(app, release, destination, progress, cancelled.Token), "download cancellation");
        }
        Assert(!Directory.GetFiles(destination, "*.part").Any(), "cancelled download partial cleanup");
        await server.StopAsync();
        await server.StartAsync(info.Port);
        await client.FetchCatalogAsync();
        await server.StopAsync();
    }

    private static async Task Serve(string root, int port)
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "cross.exe"); File.WriteAllBytes(source, Enumerable.Range(0, 180000).Select(i => (byte)(i % 239)).ToArray());
        var store = new CatalogStore(Path.Combine(root, "catalog"));
        await store.PublishAsync("cross", "Cross runtime", "", "1.0", "", source);
        await store.PublishAsync("cross", "Cross runtime", "", "1.0", "", source, "win7");
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(root, "identity"));
        using var server = new CatalogServer(store, identity);
        await server.StartAsync(port);
        File.WriteAllText(Path.Combine(root, "pairing.txt"), identity.CreatePairing("127.0.0.1", port).Export());
        Console.WriteLine("READY");
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!File.Exists(Path.Combine(root, "stop")) && DateTime.UtcNow < deadline) await Task.Delay(100);
        await server.StopAsync();
    }

    private static async Task Client(string root)
    {
        using var client = new CatalogClient(PairingInfo.Parse(File.ReadAllText(Path.Combine(root, "pairing.txt"))));
        var catalog = await client.FetchCatalogAsync();
        foreach (var release in catalog.Apps.Single().Releases)
        {
            var file = await client.DownloadAsync(catalog.Apps.Single(), release, Path.Combine(root, "download"));
            Assert(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "cross.exe"))), "cross runtime " + release.Platform);
        }
        Console.WriteLine("PASS: cross-runtime catalog and both platform packages");
    }

    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); }
    private static async Task Reject(Func<Task> operation, string name)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException or ObjectDisposedException or SocketException) { return; }
        throw new Exception("FAIL: should reject " + name);
    }
    private sealed class CallbackProgress(Action<int> callback) : IProgress<int> { public void Report(int value) => callback(value); }
}
