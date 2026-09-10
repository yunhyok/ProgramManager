using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProgramManager;
using ProgramManager.Core;

internal static class ManagerUpdaterChecks
{
    public static void Run(string root)
    {
        var catalog = new Catalog { Apps = [new CatalogApp { Id = "manager", Name = "Program Manager", GitHubRepository = ManagerUpdater.Repository,
            Releases = [Release("0.3.0", Platforms.Modern), Release("0.3.0", Platforms.Legacy), Release("0.10.0", Platforms.Modern)] }] };
        Assert(ManagerUpdater.Select(catalog, "0.2.2", Platforms.Modern)!.Release.Version == "0.10.0", "numeric newest version");
        Assert(ManagerUpdater.Select(catalog, "0.2.2", Platforms.Legacy)!.Release.FileName.EndsWith("-win7.exe"), "Windows 7 installer selected");
        Assert(ManagerUpdater.Select(catalog, "0.10", Platforms.Modern) is null && ManagerUpdater.Select(catalog, "1.0", Platforms.Modern) is null, "equal version and downgrade ignored");
        var selected = ManagerUpdater.Select(catalog, "0.2.2", Platforms.Modern)!;
        var altered = Clone(catalog);
        altered.Apps[0].Releases[2].Size++;
        Reject(() => selected.RequireSame(altered), "release replacement between check and download");
        foreach (var change in new Action<AppRelease>[] { r => r.FileName = "Other-Setup.exe", r => r.FileName = "ProgramManager-Setup-0.9.0.exe", r => r.FileName = "ProgramManager-Setup-0.10.0-win7.exe", r => r.Sha256 = "" })
        {
            altered = Clone(catalog); change(altered.Apps[0].Releases[2]);
            Reject(() => ManagerUpdater.Select(altered, "0.2.2", Platforms.Modern), "unsafe update candidate");
        }
        altered = Clone(catalog); altered.Apps[0].GitHubRepository = "other/ProgramManager";
        Assert(ManagerUpdater.Select(altered, "0.2.2", Platforms.Modern) is null, "unrelated repository ignored");
        altered = Clone(catalog); altered.Apps[0].Releases.RemoveAll(r => r.Platform == Platforms.Legacy);
        Assert(ManagerUpdater.Select(altered, "0.2.2", Platforms.Legacy) is null, "no platform fallback");
        Assert(ManagerUpdater.ShouldNotify("0.3.0", "") && ManagerUpdater.ShouldNotify("0.10.0", "0.3") &&
            !ManagerUpdater.ShouldNotify("0.3.0", "0.3") && !ManagerUpdater.ShouldNotify("0.3.0", "0.4"), "new version balloon only once");
        var state = new AppState(Path.Combine(root, "manager-ui"));
        Assert(state.Settings.ManagerAutoCheck, "old settings default to automatic checks");
        var next = AppState.Clone(state.Settings); next.ManagerAutoCheck = false; next.ManagerNotifiedVersion = "0.3.0"; state.CommitSettings(next);
        var reloaded = new AppState(state.Root);
        Assert(!reloaded.Settings.ManagerAutoCheck && reloaded.Settings.ManagerNotifiedVersion == "0.3.0", "update settings survive restart");
        using (var form = new MainForm(reloaded, false))
        using (var menu = new ContextMenuStrip())
        {
            form.PopulateTrayMenu(menu);
            Assert(menu.Items.Cast<ToolStripItem>().Count(i => i.Text == "업데이트 확인" && i.Enabled) == 1, "one manual check covers both update sources");
            Assert(!menu.Items.Cast<ToolStripItem>().Any(i => i.Text?.Contains("0개") == true), "no empty update menu");
            typeof(MainForm).GetField("_managerUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, selected);
            form.PopulateTrayMenu(menu);
            var updates = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "업데이트 1개");
            Assert(updates.DropDownItems.Cast<ToolStripItem>().Any(i => i.Text?.Contains("Program Manager " + Program.Version + " → 0.10.0") == true), "self update is named inside the shared update menu");
            typeof(MainForm).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
            form.PopulateTrayMenu(menu);
            Assert(menu.Items.Cast<ToolStripItem>().Where(i => i.Text?.StartsWith("업데이트", StringComparison.Ordinal) == true).All(i => !i.Enabled), "no concurrent update install or check");
        }
        CheckCombined(root);
        Task.Run(() => DownloadCheck(root)).GetAwaiter().GetResult();
        Console.WriteLine("PASS: manager update source/platform/version/digest policy, one-time notification, persisted options, tray actions and on-demand download");
    }

    private static void CheckCombined(string root)
    {
        var source = new Source { Version = "0.10.0" };
        using var updater = new ManagerUpdater(Path.Combine(root, "combined-manager"), new GitHubApi("", source));
        var store = new CatalogStore(Path.Combine(root, "combined-catalog"));
        var package = Path.Combine(root, "combined-package.exe"); File.WriteAllBytes(package, [0x4d, 0x5a, 1, 2]);
        Task.Run(() => store.PublishAsync("example", "Example", "", "1.0", "", package)).GetAwaiter().GetResult();
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(root, "combined-identity"));
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var server = new CatalogServer(store, identity) { ManagerUpdates = updater.Store, RefreshManagerUpdatesAsync = (force, token) => updater.RefreshAsync(force, token) };
        Task.Run(() => server.StartAsync(port)).GetAwaiter().GetResult();
        var state = new AppState(Path.Combine(root, "combined-client"));
        var settings = AppState.Clone(state.Settings);
        settings.AppAutoCheck = settings.ManagerAutoCheck = false;
        settings.ManagerNotifiedVersion = "0.10.0"; // Test lookup without displaying a real desktop notification.
        settings.PairingProtected = Secrets.Protect(identity.CreatePairing("127.0.0.1", port).Export()); state.CommitSettings(settings);
        using var form = new MainForm(state, false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void WaitFor(string field)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while ((bool)typeof(MainForm).GetField(field, flags)!.GetValue(form)! && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
            Assert(!(bool)typeof(MainForm).GetField(field, flags)!.GetValue(form)!, field + " finishes");
        }
        form.Show(); Application.DoEvents(); WaitFor("_busy");
        using var menu = new ContextMenuStrip(); form.PopulateTrayMenu(menu);
        menu.Items.Cast<ToolStripItem>().Single(i => i.Text == "업데이트 확인").PerformClick();
        form.PopulateTrayMenu(menu);
        Assert(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "업데이트 확인 중…" && !i.Enabled), "combined check prevents duplicate clicks");
        WaitFor("_checkingUpdates");
        Assert(state.Cache.Catalog.Apps.Single().Id == "example" && source.Releases > 0 && source.Assets == 0, "one tray action fetches both catalogs without downloading installers");
        Assert(CatalogRules.Version(((ManagerUpdate)typeof(MainForm).GetField("_managerUpdate", flags)!.GetValue(form)!).Release.Version) == CatalogRules.Version("0.10.0"), "new self version remains selectable without starting installation");
        var tray = (NotifyIcon)typeof(MainForm).GetField("_tray", flags)!.GetValue(form)!;
        Assert(tray.Text.Contains("업데이트 1개") && tray.Icon != form.Icon, "self updates share the badge and count");
        Task.Run(server.StopAsync).GetAwaiter().GetResult();
        Console.WriteLine("PASS: single tray check fetches app and Manager catalogs through pinned host; no automatic installer download");
    }

    private static async Task DownloadCheck(string root)
    {
        var source = new Source();
        using var updater = new ManagerUpdater(Path.Combine(root, "manager-download"), new GitHubApi("", source));
        var update = await updater.CheckAsync(null, "0.2.2", false, CancellationToken.None);
        Assert(update != null && source.Assets == 0, "update discovery downloads metadata only");
        await updater.CheckAsync(null, "0.2.2", false, CancellationToken.None);
        Assert(source.Releases == 1, "automatic check has six-hour metadata cache");
        var path = await updater.DownloadAsync(update!, null, CancellationToken.None);
        Assert(File.ReadAllBytes(path).SequenceEqual(source.Body) && source.Assets == 1 && source.Releases == 2, "download refreshes metadata and verifies bytes");
        source.Changed = true;
        try { await updater.DownloadAsync(update!, null, CancellationToken.None); throw new Exception("changed update accepted"); }
        catch (InvalidDataException) { }
        Assert(source.Assets == 1 && File.ReadAllBytes(path).SequenceEqual(source.Body), "changed release cannot replace verified download");
    }

    private static AppRelease Release(string version, string platform) => new() { Version = version, Platform = platform,
        FileName = "ProgramManager-Setup-" + version + (platform == Platforms.Legacy ? "-win7" : "") + ".exe", Size = 123,
        Sha256 = new string('A', 64), GitHubAssetId = platform == Platforms.Legacy ? 2 : 1, GitHubTag = "v" + version, PublishedUtc = DateTimeOffset.UtcNow };
    private static Catalog Clone(Catalog value) => JsonSerializer.Deserialize<Catalog>(JsonSerializer.Serialize(value))!;
    private static void Assert(bool value, string message) { if (!value) throw new Exception("ManagerUpdater: " + message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("ManagerUpdater accepted " + message); }

    private sealed class Source : HttpMessageHandler
    {
        public readonly byte[] Body = Encoding.UTF8.GetBytes("installer fixture validated without execution");
        public int Assets, Releases;
        public string Version = "0.3.0";
        public bool Changed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert(request.Headers.Authorization is null, "public update needs no credentials");
            var path = request.RequestUri!.AbsolutePath;
            object payload;
            if (path == "/repos/" + ManagerUpdater.Repository) payload = new { full_name = ManagerUpdater.Repository, description = "Program Manager", @private = false };
            else if (path == "/repos/" + ManagerUpdater.Repository + "/releases")
            {
                Releases++;
                using var sha = SHA256.Create();
                var hash = BitConverter.ToString(sha.ComputeHash(Body)).Replace("-", "");
                payload = new[] { new { draft = false, prerelease = false, tag_name = "v" + Version, body = "Update", published_at = "2026-09-10T00:00:00Z",
                    assets = new[] { new { id = Changed ? 99 : 1, name = "ProgramManager-Setup-" + Version + ".exe", size = Body.Length, digest = "sha256:" + hash } } } };
            }
            else if (path == "/repos/" + ManagerUpdater.Repository + "/releases/assets/1")
            { Assets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) }); }
            else throw new Exception("Unexpected self-update request " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
}
