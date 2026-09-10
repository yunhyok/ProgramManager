using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProgramManager.Core;

internal static class GitHubChecks
{
    public static async Task RunAsync(string root)
    {
        await AtomicReplaceAsync(root);
        var source = new FakeGitHub();
        using var api = new GitHubApi("fixture-secret", source);
        var store = new CatalogStore(Path.Combine(root, "github-cache")) { GitHub = api };
        var selected = new[] { new GitHubSelection { Repository = "owner/program" } };
        Check(await api.GetCurrentUserAsync() == "owner", "authenticated owner");
        var repositories = await api.ListRepositoriesAsync("owner");
        Check(repositories.Count == 1 && repositories[0].Private, "private repository listing");
        var catalog = await store.RefreshGitHubAsync(selected);
        var app = catalog.Apps.Single();
        Check(app.Releases.Count == 2 && app.Releases.Single(r => r.Platform == "win7").FileName.Contains("win7"), "separate OS selection");
        Check(app.Releases.Single(r => r.Platform == "win7").FileName.Contains("x86"), "Win7 x86 installer selected");
        Check(source.AssetRequests == 0 && source.ReadmeRequests == 0, "metadata synchronization does not predownload");
        Check(app.Releases.All(r => r.Sha256 == ""), "old GitHub release without digest accepted");
        using (var first = await store.PreparePackageAsync(app.Id, "1.2.0", "win10-x64"))
        using (var second = await store.PreparePackageAsync(app.Id, "1.2", "win10-x64"))
        {
            Check(source.AssetRequests == 1, "second reader uses one download");
            Check(first.Release.Sha256 == Hash(source.Package), "resolved SHA-256");
            store.ClearCache();
            Check(first.Content.Length == source.Package.Length && second.Content.ReadByte() == source.Package[0], "cleanup skips leased package");
            Check(Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.exe").Length == 1, "active file retained");
        }
        store.GitHub = null;
        using (var cached = await store.PreparePackageAsync(app.Id, "1.2", "win10-x64")) Check(cached.Content.Length == source.Package.Length, "cached installer works without GitHub");
        store.ClearCache();
        Check(Directory.GetFiles(Path.Combine(root, "github-cache", "temp")).Length == 0, "idle cache removed");
        await Reject(() => store.PreparePackageAsync(app.Id, "1.2", "win10-x64"), "evicted package needs host internet");
        store.GitHub = api;
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.PreparePackageAsync(app.Id, "1.2", "win10-x64")));
        foreach (var prepared in concurrent) prepared.Dispose();
        Check(source.AssetRequests == 2, "concurrent requests deduplicate download");
        var packagePath = Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.exe").Single();
        var corrupted = source.Package.ToArray(); corrupted[0] ^= 1; File.WriteAllBytes(packagePath, corrupted);
        using (var restored = await store.PreparePackageAsync(app.Id, "1.2", "win10-x64")) Check(restored.Content.ReadByte() == source.Package[0], "corrupt cache redownloaded");
        Check(source.AssetRequests == 3, "digest sidecar detects cache corruption");
        var docs = Encoding.UTF8.GetString(await store.FetchDocumentationAsync(app.Id));
        Check(docs.Contains("&lt;script&gt;") && !docs.Contains("<script>") && docs.Contains("Content-Security-Policy") && docs.Contains("<h3>Readme heading</h3>"), "README becomes inert readable HTML");
        Check(docs.Contains("배포 버전과 변경 이력") && docs.Contains("win7"), "offline history includes platform variants");
        store.GitHub = null;
        var docsPath = Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.html").Single();
        File.SetLastWriteTimeUtc(docsPath, DateTime.UtcNow.AddDays(-2));
        Check((await store.FetchDocumentationAsync(app.Id)).Length > 0 && source.ReadmeRequests == 1, "description reused offline");
        store.GitHub = api;
        source.Ambiguous = true;
        await Reject(() => store.RefreshGitHubAsync(selected), "ambiguous installer rejected");
        Check(store.Read().Apps.Single().Releases.Count == 2, "refresh failure keeps last known catalog");
        await store.RefreshGitHubAsync(new[] { new GitHubSelection { Repository = "owner/program", ModernAssetPattern = "Program-Setup.exe" } });
        source.Ambiguous = false;
        source.Legacy = false;
        catalog = await store.RefreshGitHubAsync(selected);
        Check(catalog.Apps.Single().Releases.All(r => r.Platform == "win10-x64"), "Win7 never receives modern fallback");
        await Reject(() => store.PreparePackageAsync(app.Id, "1.2", "win7"), "missing legacy installer rejected");
        source.Digest = new string('a', 64);
        await store.RefreshGitHubAsync(selected);
        await Reject(() => store.PreparePackageAsync(app.Id, "1.2", "win10-x64"), "upstream digest mismatch rejected");
        Check(Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.part").Length == 0, "failed download leaves no partial file");
        source.Digest = "";
        source.BlockDownloads = true;
        await store.RefreshGitHubAsync(selected);
        store.ClearCache();
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = store.PreparePackageAsync(app.Id, "1.2", "win10-x64", cancellation.Token);
            Check(await Task.WhenAny(source.DownloadStarted.Task, Task.Delay(5000)) == source.DownloadStarted.Task, "download started");
            store.ClearCache();
            Check(Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.part").Length == 1, "cleanup skips in-progress download");
            cancellation.Cancel();
            try { using var unexpected = await pending; throw new Exception("Cancellation should stop download"); } catch (OperationCanceledException) { }
            Check(Directory.GetFiles(Path.Combine(root, "github-cache", "temp"), "*.part").Length == 0, "cancellation removes partial download");
        }
        source.BlockDownloads = false;
        source.Digest = "";
        source.WrongSize = true;
        await store.RefreshGitHubAsync(selected);
        store.ClearCache();
        await Reject(() => store.PreparePackageAsync(app.Id, "1.2", "win10-x64"), "upstream size mismatch rejected");
        using (var redirected = new GitHubApi("fixture-secret", new RedirectHandler(false)))
        using (var bytes = new MemoryStream()) await redirected.DownloadAssetAsync("owner/program", 10, bytes, 3);
        using (var hostile = new GitHubApi("fixture-secret", new RedirectHandler(true)))
        using (var bytes = new MemoryStream()) await Reject(() => hostile.DownloadAssetAsync("owner/program", 10, bytes, 3), "non-GitHub redirect rejected");
        await GracefulDisposeAsync();
        var outside = Path.Combine(root, "keep.txt"); File.WriteAllText(outside, "keep"); store.ClearCache();
        Check(File.ReadAllText(outside) == "keep", "cache cleanup confined to host temp");
        Console.WriteLine("GitHub metadata, selection, lazy cache, integrity, offline HTML, redirect and graceful disposal checks passed.");
    }

    private static async Task AtomicReplaceAsync(string root)
    {
        var source = Path.Combine(root, "replace-new.txt");
        var target = Path.Combine(root, "replace-existing.txt");
        File.WriteAllText(source, "new contents"); File.WriteAllText(target, "original contents");
        var method = typeof(Catalog).Assembly.GetType("ProgramManager.Core.Compat")!.GetMethod("Replace")!;
        var replace = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), method);
        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { File.Replace(source, target, null); throw new Exception("Raw replacement unexpectedly bypassed a delete-sharing lock"); }
            catch (IOException ex)
            {
                Console.WriteLine("Atomic replace native lock HRESULT: 0x" + ex.HResult.ToString("X8"));
                Check((uint)ex.HResult is 0x80070020 or 0x80070021 or 0x80070497, "native lock is an eligible transient replacement error");
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { replace(source, target); throw new Exception("Permanent lock unexpectedly allowed replacement"); } catch (IOException) { }
            Check(watch.Elapsed < TimeSpan.FromSeconds(5), "permanently locked replacement has bounded retry");
            Check(File.ReadAllText(source) == "new contents" && File.ReadAllText(target) == "original contents", "permanent lock preserves both source and target");
        }
        var transient = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Run(() => { Thread.Sleep(180); transient.Dispose(); });
        try { replace(source, target); } finally { await released; }
        Check(!File.Exists(source) && File.ReadAllText(target) == "new contents", "replacement succeeds after another thread releases a transient lock");
        Console.WriteLine("PASS: atomic replacement retries transient sharing failures and preserves files on permanent lock");
    }

    private static async Task GracefulDisposeAsync()
    {
        foreach (var kind in new[] { "asset", "readme", "repositories" })
        {
            var handler = new PausedBodyHandler(kind);
            using var original = new GitHubApi("fixture-secret", handler);
            using var replacement = new GitHubApi("fixture-secret", new FakeGitHub());
            using var output = new MemoryStream();
            Task pending = kind == "asset" ? original.DownloadAssetAsync("owner/program", 10, output, 3) : kind == "readme" ? original.GetReadmeAsync("owner/program") : original.ListRepositoriesAsync("owner");
            Check(await Task.WhenAny(handler.Body.Started.Task, Task.Delay(5000)) == handler.Body.Started.Task, kind + " response body reading started");
            original.Dispose(); original.Dispose();
            Check(!handler.Disposed, kind + " active operation retains HTTP client");
            try { await original.GetCurrentUserAsync(); throw new Exception("Disposed API accepted a new operation"); } catch (ObjectDisposedException) { }
            Check(await replacement.GetCurrentUserAsync() == "owner", "replacement API accepts new operations");
            handler.Body.Continue.TrySetResult(true);
            await pending;
            Check(handler.Disposed, kind + " HTTP client disposed after complete body processing");
            if (kind == "asset") Check(output.ToArray().SequenceEqual(new byte[] { 1, 2, 3 }), "API swap preserves complete installer bytes");
            if (kind == "readme") Check(await (Task<string>)pending == "# Offline README", "API swap preserves README body");
            if (kind == "repositories") Check(handler.Requests == 2 && (await (Task<List<GitHubRepository>>)pending).Count == 1, "active pagination continues after disposal request");
        }
    }

    private static void Check(bool condition, string name) { if (!condition) throw new Exception("GitHub check failed: " + name); }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException) { return; }
        throw new Exception("GitHub check should reject: " + name);
    }
    private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly byte[] Package = Encoding.UTF8.GetBytes("fixture installer binary");
        public int AssetRequests, ReadmeRequests;
        public bool Ambiguous, WrongSize, BlockDownloads;
        public readonly TaskCompletionSource<bool> DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Legacy = true;
        public string Digest = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Headers.Authorization?.Parameter == "fixture-secret", "API request authenticated");
            var path = request.RequestUri!.AbsolutePath;
            object payload;
            if (path == "/user") payload = new { login = "owner" };
            else if (path == "/user/repos") payload = new[] { new { full_name = "owner/program", description = "Private example", @private = true } };
            else if (path == "/repos/owner/program") payload = new { full_name = "owner/program", description = "Private example", @private = true };
            else if (path == "/repos/owner/program/releases")
            {
                var assets = new List<object> { new { id = 10, name = "Program-Setup.exe", size = Package.Length, digest = Digest == "" ? null : "sha256:" + Digest } };
                if (Legacy) assets.Add(new { id = 11, name = "Program-Setup-win7-x86.exe", size = Package.Length, digest = (string?)null });
                if (Ambiguous) assets.Add(new { id = 12, name = "Alternate-Setup.exe", size = Package.Length, digest = (string?)null });
                payload = new[] { new { draft = false, prerelease = false, tag_name = "v1.2.0", body = "# Changes\nNew version <img src=https://example.com/tracker>", published_at = "2026-09-10T00:00:00Z", assets } };
            }
            else if (path == "/repos/owner/program/readme")
            {
                ReadmeRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Readme heading\n<script>alert(1)</script>\n[Link](https://example.com)\n```\ncode\n```", Encoding.UTF8) });
            }
            else if (path.StartsWith("/repos/owner/program/releases/assets/", StringComparison.Ordinal))
            {
                AssetRequests++;
                if (BlockDownloads) return BlockAsync(cancellationToken);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(WrongSize ? Package.Take(3).ToArray() : Package) });
            }
            else throw new Exception("Unexpected fake GitHub route: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
        private async Task<HttpResponseMessage> BlockAsync(CancellationToken token)
        {
            DownloadStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("Blocked fixture was not cancelled");
        }
    }

    private sealed class RedirectHandler(bool hostile) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Check(request.Headers.Authorization?.Parameter == "fixture-secret", "redirect origin has token");
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri(hostile ? "https://example.com/asset" : "https://release-assets.githubusercontent.com/asset");
                return Task.FromResult(response);
            }
            Check(request.RequestUri.Host == "release-assets.githubusercontent.com" && request.Headers.Authorization is null, "GitHub token never forwarded to download CDN");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        }
    }

    private sealed class PausedBodyHandler : HttpMessageHandler
    {
        public readonly PausedBody Body;
        public bool Disposed;
        public int Requests;
        public PausedBodyHandler(string kind)
        {
            var bytes = kind == "asset" ? new byte[] { 1, 2, 3 } : Encoding.UTF8.GetBytes(kind == "readme" ? "# Offline README" : JsonSerializer.Serialize(Enumerable.Range(0, 100).Select(_ => new { full_name = "owner/program", description = "Example", @private = true })));
            Body = new PausedBody(bytes);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(!Disposed, "active pagination uses a live HTTP handler");
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Requests == 1 ? new StreamContent(Body) : new StringContent("[]") });
        }
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; Body.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class PausedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            using var registration = cancellationToken.Register(() => Continue.TrySetCanceled());
            await Continue.Task;
            return await base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}
