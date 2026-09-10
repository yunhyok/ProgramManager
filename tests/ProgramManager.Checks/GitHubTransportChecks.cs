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
        await server.StopAsync();
        Console.WriteLine("PASS: GitHub transport identity/hash/snapshot, offline HTML integrity, failure cleanup, legacy compatibility and safe server errors");
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or ObjectDisposedException or SocketException) { return ex; }
        throw new Exception("FAIL: should reject " + message);
    }
    private sealed class ProgressCallback(Action<int> callback) : IProgress<int> { public void Report(int value) => callback(value); }
}
