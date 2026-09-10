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
            Assert(menu.Items.Cast<ToolStripItem>().Any(i => i.Text == "관리 프로그램 업데이트 확인" && i.Enabled), "manual check is always available");
            typeof(MainForm).GetField("_managerUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, selected);
            form.PopulateTrayMenu(menu);
            Assert(menu.Items.Cast<ToolStripItem>().Any(i => i.Text?.Contains("0.10.0 업데이트 및 재시작") == true && i.Enabled), "available version has actionable tray menu");
            typeof(MainForm).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
            form.PopulateTrayMenu(menu);
            Assert(menu.Items.Cast<ToolStripItem>().Where(i => i.Text?.Contains("업데이트 및 재시작") == true || i.Text == "관리 프로그램 업데이트 확인").All(i => !i.Enabled), "no concurrent update install");
        }
        Task.Run(() => DownloadCheck(root)).GetAwaiter().GetResult();
        Console.WriteLine("PASS: manager update source/platform/version/digest policy, one-time notification, persisted options, tray actions and on-demand download");
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
                payload = new[] { new { draft = false, prerelease = false, tag_name = "v0.3.0", body = "Update", published_at = "2026-09-10T00:00:00Z",
                    assets = new[] { new { id = Changed ? 99 : 1, name = "ProgramManager-Setup-0.3.0.exe", size = Body.Length, digest = "sha256:" + hash } } } };
            }
            else if (path == "/repos/" + ManagerUpdater.Repository + "/releases/assets/1")
            { Assets++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Body) }); }
            else throw new Exception("Unexpected self-update request " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
}
