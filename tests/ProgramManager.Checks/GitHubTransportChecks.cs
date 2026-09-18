using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProgramManager.Core;

internal static class GitHubTransportChecks
{
    public static async Task RunAsync(string root)
    {
        var folder = Path.Combine(root, "github-transport");
        Directory.CreateDirectory(folder);
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(folder, "identity"));
        var body = Enumerable.Range(0, 180000).Select(i => (byte)(i % 239)).ToArray();
        var release = new AppRelease { Version = "2.0", Platform = "win10-x64", FileName = "Setup.exe", Size = body.Length, PublishedUtc = DateTimeOffset.UtcNow, GitHubAssetId = 123, GitHubTag = "v2.0" };
        var app = new CatalogApp { Id = "github-app", Name = "GitHub program", GitHubRepository = "owner/program", Releases = [release] };
        var catalog = new Catalog { Apps = [app] };
        var actual = Copy(release); actual.Sha256 = Hash(body);
        var package = new Header { Id = app.Id, GitHubRepository = app.GitHubRepository, Size = body.Length, Release = actual };
        var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>설명</title><h1>오프라인 설명</h1>");
        var documentation = new Header { Operation = "documentation", Id = app.Id, GitHubRepository = app.GitHubRepository, Size = html.Length, Sha256 = Hash(html) };
        using var host = new Peer(identity);
        host.Response = operation => operation == "catalog" ? (new Header { Catalog = catalog }, Array.Empty<byte>()) : operation == "documentation" ? (documentation, html) : (package, body);
        using var client = new CatalogClient(host.Info);
        var returned = (await client.FetchCatalogAsync()).Apps.Single();
        var output = Path.Combine(folder, "downloads");
        var downloaded = await client.DownloadAsync(returned, returned.Releases[0], output);
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(body) && Path.GetFileName(downloaded).Contains(actual.Sha256.Substring(0, 12)), "digestless GitHub asset uses authenticated actual digest");
        Assert(host.SawCapabilities, "new client advertises GitHub capability");
        var docPath = await client.DownloadDocumentationAsync(returned, output);
        Assert(File.ReadAllBytes(docPath).SequenceEqual(html) && Path.GetExtension(docPath) == ".html", "offline HTML transfer");

        var mismatches = new Action<Header>[]
        {
            h => h.Id = "other-app", h => h.GitHubRepository = "owner/other",
            h => h.Release!.GitHubAssetId++, h => h.Release!.GitHubTag = "v2.1",
            h => h.Release!.Version = "2.1", h => h.Release!.Platform = "win7",
            h => h.Release!.FileName = "Other.exe", h => h.Release!.Size++,
            h => h.Release!.PublishedUtc = h.Release.PublishedUtc.AddSeconds(1),
            h => h.Release!.Sha256 = "", h => h.Size++
        };
        foreach (var change in mismatches)
        {
            var changed = Copy(package); change(changed);
            host.Response = _ => (changed, body);
            await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output), "changed immutable package identity");
        }
        returned.Releases[0].GitHubAssetId++;
        var mutated = Copy(package); mutated.Release!.GitHubAssetId++;
        host.Response = _ => (mutated, body);
        await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output), "returned object cannot change private catalog snapshot");
        returned.Releases[0].GitHubAssetId--;
        host.Response = _ => (package, body.Select(b => (byte)(b ^ 1)).ToArray());
        await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output), "package corruption");
        host.Response = _ => (package, body.Take(100).ToArray());
        await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output), "truncated package");
        host.Response = _ => (package, body);
        using (var cancelled = new CancellationTokenSource())
            await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output, new ProgressCallback(_ => cancelled.Cancel()), cancelled.Token), "GitHub package cancellation");
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(body), "failed download preserves verified installer");

        foreach (var change in new Action<Header>[] { h => h.Id = "other", h => h.GitHubRepository = "owner/other", h => h.Size = CatalogRules.MaxDocumentationBytes + 1L, h => h.Sha256 = "bad" })
        {
            var changed = Copy(documentation); change(changed);
            host.Response = _ => (changed, html);
            await Reject(() => client.DownloadDocumentationAsync(returned, output), "invalid documentation identity or bounds");
        }
        host.Response = _ => (documentation, html.Select(b => (byte)(b ^ 1)).ToArray());
        await Reject(() => client.DownloadDocumentationAsync(returned, output), "documentation corruption");
        Assert(File.ReadAllBytes(docPath).SequenceEqual(html), "failed documentation preserves verified HTML");
        Assert(!Directory.GetFiles(output, "*.part").Any(), "all failed and cancelled transfer partials removed");

        release.Sha256 = new string('A', 64);
        host.Response = operation => operation == "catalog" ? (new Header { Catalog = catalog }, Array.Empty<byte>()) : (package, body);
        returned = (await client.FetchCatalogAsync()).Apps.Single();
        returned.Releases[0].Sha256 = actual.Sha256;
        await Reject(() => client.DownloadAsync(returned, returned.Releases[0], output), "actual digest must match immutable catalog digest when present");

        // New client interoperates with the size-only response from a 0.1.x manual host.
        app.GitHubRepository = ""; release.GitHubAssetId = 0; release.GitHubTag = ""; release.Sha256 = actual.Sha256;
        host.Response = operation => operation == "catalog" ? (new Header { Catalog = catalog }, Array.Empty<byte>()) : (new Header { Size = body.Length }, body);
        returned = (await client.FetchCatalogAsync()).Apps.Single();
        Assert(File.ReadAllBytes(await client.DownloadAsync(returned, returned.Releases[0], output)).SequenceEqual(body), "legacy host manual transfer");

        // The real server tells an old client to upgrade before sending new catalog metadata.
        app.GitHubRepository = "owner/program"; release.GitHubAssetId = 123; release.GitHubTag = "v2.0"; release.Sha256 = "";
        var storePath = Path.Combine(folder, "store");
        var upstream = new AssetSource(body);
        using var api = new GitHubApi("", upstream);
        var store = new CatalogStore(storePath) { GitHub = api };
        JsonFiles.Write(Path.Combine(storePath, "catalog.json"), catalog);
        var info = identity.CreatePairing("127.0.0.1", FreePort());
        using var server = new CatalogServer(store, identity);
        await server.StartAsync(info.Port);
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(info.Host, info.Port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && Hash(cert.GetRawCertData()) == info.Fingerprint);
            await tls.AuthenticateAsClientAsync(info.Host, null, SslProtocols.Tls12, false);
            await Write(tls, new { Protocol = 1, Operation = "catalog", Token = info.Token });
            using var response = await Read(tls);
            Assert(response.RootElement.GetProperty("ErrorCode").GetString() == "client_update_required", "old client gets upgrade explanation");
        }
        using (var connected = new CatalogClient(info))
        {
            var beforeRemoval = (await connected.FetchCatalogAsync()).Apps.Single();
            Assert(upstream.AssetRequests == 0 && upstream.ReadmeRequests == 0, "real server catalog does not download files");
            var actualFile = await connected.DownloadAsync(beforeRemoval, beforeRemoval.Releases.Single(), output);
            Assert(File.ReadAllBytes(actualFile).SequenceEqual(body), "GitHub source through real cache and pinned TLS client");
            var actualPage = await connected.DownloadDocumentationAsync(beforeRemoval, output);
            Assert(File.ReadAllText(actualPage).Contains("Offline readme") && upstream.ReadmeRequests == 1, "host converts and transfers offline README");
            store.GitHub = null;
            await connected.DownloadAsync(beforeRemoval, beforeRemoval.Releases.Single(), output);
            await connected.DownloadDocumentationAsync(beforeRemoval, output);
            Assert(upstream.AssetRequests == 1 && upstream.ReadmeRequests == 1, "real server serves cache without internet");
            store.ClearCache();
            var missingCache = await Reject(() => connected.DownloadAsync(beforeRemoval, beforeRemoval.Releases.Single(), output), "missing offline cache is explained");
            Assert(missingCache.Message.Contains("GitHub") && !missingCache.Message.Contains("https:"), "upstream failure is useful without exposing URLs");
            JsonFiles.Write(Path.Combine(storePath, "catalog.json"), new Catalog());
            var error = await Reject(() => connected.DownloadAsync(beforeRemoval, beforeRemoval.Releases.Single(), output), "removed upstream app is explained");
            Assert(error.Message.Contains("새로고침"), "server preparation failure returns helpful text");
        }
        // Archive transfer uses the same verified cache/TLS path, while old clients keep a valid installer-only catalog.
        store.GitHub = api;
        release.FileName = "Tool.zip";
        JsonFiles.Write(Path.Combine(storePath, "catalog.json"), catalog);
        using (var archives = new CatalogClient(info))
        {
            var archiveApp = (await archives.FetchCatalogAsync()).Apps.Single();
            Assert(archiveApp.Latest!.IsArchive, "archive-aware client receives ZIP metadata");
            var archivePath = await archives.DownloadAsync(archiveApp, archiveApp.Latest, output);
            Assert(Path.GetExtension(archivePath) == ".zip" && File.ReadAllBytes(archivePath).SequenceEqual(body), "ZIP receives size/hash verified bytes without execution");
            archiveApp.Latest.FileName = "../../hostile.zip";
            await Reject(() => Task.FromResult(CatalogRules.Validate(new Catalog { Apps = [archiveApp] })), "ZIP filename traversal rejected");
        }
        foreach (var capability in new[] { 0, 1 })
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(info.Host, info.Port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert is not null && Hash(cert.GetRawCertData()) == info.Fingerprint);
            await tls.AuthenticateAsClientAsync(info.Host, null, SslProtocols.Tls12, false);
            await Write(tls, new { Protocol = 1, Operation = "catalog", Token = info.Token, Capabilities = capability });
            using var response = await Read(tls);
            if (capability == 0) Assert(response.RootElement.GetProperty("ErrorCode").GetString() == "client_update_required", "pre-GitHub client gets required upgrade even for ZIP-only catalog");
            else Assert(response.RootElement.GetProperty("Catalog").GetProperty("Apps").GetArrayLength() == 0 && response.RootElement.GetProperty("RefreshWarning").GetString()!.Contains("ZIP"), "older GitHub clients get a valid catalog and ZIP upgrade guidance");
        }
        await server.StopAsync();
        await ManagerUpdateRoutingAsync(folder, identity, body);
        Console.WriteLine("PASS: GitHub transport identity/hash/snapshot, offline HTML integrity, failure cleanup, legacy compatibility and safe server errors");
    }

    private static async Task ManagerUpdateRoutingAsync(string folder, HostIdentity identity, byte[] body)
    {
        var regularStore = new CatalogStore(Path.Combine(folder, "regular-store"));
        var localInstaller = Path.Combine(folder, "Regular-Setup.exe"); File.WriteAllBytes(localInstaller, body);
        await regularStore.PublishAsync("regular-app", "Regular app", "Normal distribution", "1.0", "", localInstaller);
        var managerRoot = Path.Combine(folder, "manager-store");
        var source = new AssetSource(body);
        using var api = new GitHubApi("", source);
        var managerStore = new CatalogStore(managerRoot) { GitHub = api };
        var release = new AppRelease { Version = "0.3.0", Platform = "win10-x64", FileName = "ProgramManager-Setup.exe", Size = body.Length, PublishedUtc = DateTimeOffset.UtcNow, GitHubAssetId = 123, GitHubTag = "v0.3.0" };
        var app = new CatalogApp { Id = "manager-app", Name = "Program Manager", GitHubRepository = "owner/program", Releases = [release] };
        var catalog = new Catalog { Apps = [app] };
        var refreshes = 0;
        var forced = false;
        using var server = new CatalogServer(regularStore, identity)
        {
            ManagerUpdates = managerStore,
            RefreshManagerUpdatesAsync = (force, token) => { token.ThrowIfCancellationRequested(); forced = force; Interlocked.Increment(ref refreshes); JsonFiles.Write(Path.Combine(managerRoot, "catalog.json"), catalog); return Task.CompletedTask; }
        };
        var info = identity.CreatePairing("127.0.0.1", FreePort());
        await server.StartAsync(info.Port);
        using var regular = new CatalogClient(info);
        using var manager = new CatalogClient(info, managerUpdates: true);
        var ordinary = (await regular.FetchCatalogAsync(forceManagerRefresh: true)).Apps.Single();
        Assert(ordinary.Id == "regular-app" && refreshes == 0, "normal catalog excludes manager updates and never refreshes manager metadata");
        var offered = (await manager.FetchCatalogAsync()).Apps.Single();
        Assert(offered.Id == app.Id && refreshes == 1 && !forced && source.AssetRequests == 0, "automatic manager catalog refresh is not forced and requests no binaries");
        var destination = Path.Combine(folder, "manager-downloads");
        var downloaded = await manager.DownloadAsync(offered, offered.Releases.Single(), destination);
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(body) && refreshes == 1 && source.AssetRequests == 1, "manager package uses dedicated store and verified hash without metadata refresh");
        await manager.DownloadAsync(offered, offered.Releases.Single(), destination);
        Assert(refreshes == 1 && source.AssetRequests == 1, "manager package reuses verified cache");
        await Reject(() => regular.DownloadAsync(offered, offered.Releases.Single(), destination), "normal snapshot cannot request manager package");
        await Reject(() => manager.DownloadAsync(ordinary, ordinary.Releases.Single(), destination), "manager snapshot cannot request normal package");
        using (var unfetched = new CatalogClient(info, managerUpdates: true)) await Reject(() => unfetched.DownloadAsync(offered, offered.Releases.Single(), destination), "manager package needs a snapshot from its own client");
        var docsError = await Reject(() => manager.DownloadDocumentationAsync(offered, destination), "manager mode refuses documentation");
        Assert(docsError.Message.Contains("설명 페이지"), "manager documentation rejection is explicit");
        var wrongToken = Copy(info); wrongToken.Token = new string('0', 64);
        using (var invalid = new CatalogClient(wrongToken, managerUpdates: true)) await Reject(() => invalid.FetchCatalogAsync(), "manager catalog requires pairing token");
        Assert(refreshes == 1, "unauthenticated manager request never invokes upstream refresh");
        var wrongPin = Copy(info); wrongPin.Fingerprint = new string('0', 64);
        using (var invalid = new CatalogClient(wrongPin, managerUpdates: true)) await Reject(() => invalid.FetchCatalogAsync(), "manager catalog requires pinned certificate");
        await manager.FetchCatalogAsync(forceManagerRefresh: true);
        Assert(refreshes == 2 && forced, "manual manager check forces host metadata refresh");
        await manager.FetchCatalogAsync();
        Assert(refreshes == 3 && !forced, "automatic manager checks resume normal cached refresh policy");
        server.ManagerUpdates = null;
        var unavailable = await Reject(() => manager.FetchCatalogAsync(), "unconfigured manager store rejected");
        Assert(unavailable.Message.Contains("업데이트 배포가 설정되지"), "unconfigured manager store has useful explanation");
        await Reject(() => manager.DownloadAsync(offered, offered.Releases.Single(), destination), "manager package cannot fall back to normal store");
        Assert((await regular.FetchCatalogAsync()).Apps.Single().Id == "regular-app", "missing manager store leaves ordinary catalog available");
        await server.StopAsync();

        // The same private snapshot and header checks apply to the manager operation prefix.
        var actual = Copy(release); actual.Sha256 = Hash(body);
        var package = new Header { Id = app.Id, GitHubRepository = app.GitHubRepository, Release = actual, Size = body.Length };
        using var peer = new Peer(identity);
        peer.Response = operation => operation == "manager-catalog" ? (new Header { Catalog = catalog }, Array.Empty<byte>()) : operation == "manager-package" ? (package, body) : throw new Exception("Unexpected manager operation: " + operation);
        using var pinnedManager = new CatalogClient(peer.Info, managerUpdates: true);
        var pinned = (await pinnedManager.FetchCatalogAsync()).Apps.Single();
        foreach (var change in new Action<Header>[] { h => h.GitHubRepository = "owner/untrusted", h => h.Release!.Platform = "win7", h => h.Release!.Sha256 = new string('A', 64), h => h.Release!.GitHubAssetId++ })
        {
            var changed = Copy(package); change(changed); peer.Response = _ => (changed, body);
            await Reject(() => pinnedManager.DownloadAsync(pinned, pinned.Releases.Single(), destination), "manager repo/platform/digest/asset mismatch rejected");
        }
        pinned.Releases[0].GitHubAssetId++;
        var mutated = Copy(package); mutated.Release!.GitHubAssetId++;
        peer.Response = _ => (mutated, body);
        await Reject(() => pinnedManager.DownloadAsync(pinned, pinned.Releases.Single(), destination), "manager caller mutation cannot change pinned snapshot");
        foreach (var errorCode in new[] { "unsupported_request", "" })
        {
            peer.Response = _ => (new Header { ErrorCode = errorCode, Error = "지원하지 않는 요청입니다." }, Array.Empty<byte>());
            var oldHost = await Reject(() => pinnedManager.FetchCatalogAsync(), "old host has no manager update endpoint");
            Assert(oldHost.Message.Contains("호스트") && oldHost.Message.Contains("0.3.0"), "0.2 and 0.1 host errors explain minimum manager version");
        }
        Assert(File.ReadAllBytes(downloaded).SequenceEqual(body) && !Directory.GetFiles(destination, "*.part").Any(), "manager failures preserve verified installer and remove partial files");
        Console.WriteLine("PASS: isolated manager update routes, authenticated refresh, snapshot/hash verification, missing and older host errors");
    }

    private sealed class AssetSource(byte[] body) : HttpMessageHandler
    {
        public int AssetRequests, ReadmeRequests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/repos/owner/program/readme")
            {
                ReadmeRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Offline readme\nInstructions for installation.") });
            }
            Assert(request.RequestUri.AbsolutePath == "/repos/owner/program/releases/assets/123", "host requests selected asset identity only");
            AssetRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class Header
    {
        public int Protocol { get; set; } = 1;
        public string Operation { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        public string Error { get; set; } = "";
        public string Id { get; set; } = "";
        public string GitHubRepository { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public AppRelease? Release { get; set; }
        public Catalog? Catalog { get; set; }
    }

    private sealed class Peer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly HostIdentity identity;
        private readonly Task serving;
        public PairingInfo Info { get; }
        public Func<string, (Header Header, byte[] Body)> Response { get; set; } = _ => throw new InvalidOperationException();
        public bool SawCapabilities { get; private set; }

        public Peer(HostIdentity identity)
        {
            this.identity = identity;
            listener.Start();
            Info = identity.CreatePairing("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
            serving = Serve();
        }

        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var close = deadline.Token.Register(client.Close);
                    using var tls = new SslStream(client.GetStream(), false);
                    try
                    {
                        await tls.AuthenticateAsServerAsync(identity.Certificate, false, SslProtocols.Tls12, false);
                        using var request = await Read(tls);
                        Assert(request.RootElement.GetProperty("Token").GetString() == identity.Token, "authenticated test request");
                        SawCapabilities |= request.RootElement.TryGetProperty("Capabilities", out var capability) && capability.GetInt32() == 1;
                        var response = Response(request.RootElement.GetProperty("Operation").GetString()!);
                        await Write(tls, response.Header);
                        await tls.WriteAsync(response.Body, 0, response.Body.Length);
                        await tls.FlushAsync();
                    }
                    catch (Exception ex) when (ex is IOException or SocketException) { /* A rejected body closes its connection. */ }
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }

        public void Dispose() { listener.Stop(); serving.GetAwaiter().GetResult(); }
    }

    private static async Task Write(Stream stream, object value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        var prefix = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(json.Length));
        await stream.WriteAsync(prefix, 0, prefix.Length);
        await stream.WriteAsync(json, 0, json.Length);
        await stream.FlushAsync();
    }

    private static async Task<JsonDocument> Read(Stream stream)
    {
        var prefix = new byte[4]; await Exact(stream, prefix);
        var length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(prefix, 0));
        Assert(length is > 0 and <= 65536, "bounded test wire message");
        var data = new byte[length]; await Exact(stream, data);
        return JsonDocument.Parse(data);
    }

    private static async Task Exact(Stream stream, byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;
    private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("FAIL: " + message); }
    private static async Task<Exception> Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or SocketException or AuthenticationException) { return ex; }
        throw new Exception("FAIL: should reject " + message);
    }
    private sealed class ProgressCallback(Action<int> callback) : IProgress<int> { public void Report(int value) => callback(value); }
}
