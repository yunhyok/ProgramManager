using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ProgramManager.Core;

internal static class GitHubAvailabilityChecks
{
    public static async Task RunAsync(string root)
    {
        var source = new Releases();
        using var api = new GitHubApi(handler: source);
        var store = new CatalogStore(Path.Combine(root, "availability")) { GitHub = api };
        var info = new GitHubRepository { FullName = "owner/tool" };
        var selection = new GitHubSelection { Repository = info.FullName };
        source.Items = [];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("GitHub Release가 없습니다"), "no releases is distinct");
        source.Items = [Release("v1.0", draft: true)];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("초안"), "unpublished draft with null publication date");
        source.Items = [Release("build-latest")];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("버전 태그"), "unsupported tag reason");
        source.Items = [Release("v1.0")];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("첨부된 배포 파일"), "no attached assets reason");
        source.Items = [Release("v1.0", names: ["tool.tar.gz"])];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("tool.tar.gz"), "unsupported formats are named");
        source.Items = [Release("v0.3.2-rc.1", preview: true, names: ["Tool-0.3.2-win64-setup.exe"])];
        var excluded = await store.CheckGitHubRepositoryAsync(info, selection);
        Check(excluded.App is null && excluded.Error.Contains("시험판") && excluded.Releases?.Count == 1, "real R2R suffix-tagged preview retained with reason");
        selection.IncludePrereleases = true;
        var cached = CatalogStore.EvaluateGitHubRepository(info, excluded.Releases!, selection);
        Check(cached.App?.Latest is { IsPrerelease: true, Version: "0.3.2", GitHubTag: "v0.3.2-rc.1" } && cached.App.Latest.Notes.Contains("시험판"), "opt in preserves original tag and warns clients without changing numeric installer versions");
        var persisted = JsonSerializer.Deserialize<GitHubSelection>(JsonSerializer.Serialize(selection))!;
        Check(persisted.IncludePrereleases && !JsonSerializer.Deserialize<GitHubSelection>("{\"Repository\":\"owner/tool\"}")!.IncludePrereleases, "preview setting survives save and old settings default off");
        source.Items = [Release("v1.0-rc.1", preview: true, day: 1, names: ["Old-Setup.exe"]), Release("v1.0-rc.2", preview: true, day: 2, names: ["New-Setup.exe"])];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App?.Latest?.FileName == "New-Setup.exe", "latest published preview wins same numeric version");
        source.Items = source.Items.Concat(new[] { Release("v1.0", day: 1, names: ["Stable-Setup.exe"]) }).ToArray();
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App?.Latest?.FileName == "Stable-Setup.exe", "stable wins same numeric version regardless of publication date");
        source.Items = [Release("v1.0", names: ["Stable-Setup.exe"]), new { tag_name = "v2.0-rc.1", draft = false, prerelease = true, published_at = "2026-09-01T00:00:00Z" }];
        selection.IncludePrereleases = false;
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App != null, "malformed excluded preview cannot break stable distribution");
        selection.IncludePrereleases = true;
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("필수 항목"), "included malformed preview is rejected with its own cause");
        source.Items = [Release("v2.0", names: ["Stable-Setup.exe", "Stable-Setup-win7.exe"]), source.Items[1]];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App?.Releases.Count == 2, "malformed same-number preview cannot reject stable releases that already cover both platforms");
        source.Items = [Release("v2.0-rc.1", names: ["Preview-Setup.exe"])];
        selection.IncludePrereleases = false;
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("시험판"), "suffix is preview even if GitHub flag is false");
        selection.IncludePrereleases = true;
        await store.RefreshGitHubAsync([selection]);
        Check(store.Read().Apps.Single().Latest!.IsPrerelease, "opted-in preview synchronizes");
        source.Fail = true; selection.IncludePrereleases = false;
        Check((await store.RefreshGitHubAsync([selection])).Catalog.Apps.Count == 0, "opt out removes previous previews even during failed refresh");
        source.Fail = false;
        source.Items = [Release("v1.0", names: ["Tool-win11-net48.zip", "Tool-linux.zip"])];
        var zip = (await store.CheckGitHubRepositoryAsync(info, selection)).App!;
        Check(zip.Releases.Single().IsArchive && zip.Latest!.Platform == "win10-x64", "explicit win11 beats net48 fallback and linux excluded");
        source.Items = [Release("v1.0", names: ["Tool-win7-win11-net48.zip"])];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App!.Releases.Count == 2, "dual Windows ZIP is offered on both platforms");
        source.Items = [Release("v1.0", names: ["Tool-Setup.exe", "Tool.zip"])];
        Check(!(await store.CheckGitHubRepositoryAsync(info, selection)).App!.Latest!.IsArchive, "setup takes priority over ZIP automatically");
        selection.ModernAssetPattern = "*.zip";
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).App!.Latest!.IsArchive, "explicit pattern can select ZIP");
        selection.ModernAssetPattern = "missing*.zip";
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("패턴"), "wrong pattern is distinct");
        selection.ModernAssetPattern = "";
        source.Items = [Release("v1.0", names: ["Master.zip", "Slave.zip"])];
        Check((await store.CheckGitHubRepositoryAsync(info, selection)).Error.Contains("여러 개"), "multiple ZIP editions require explicit choice");
        Console.WriteLine("PASS: release diagnostics, per-repository preview opt-in/opt-out, stable precedence, ZIP and installer selection");
    }

    private static object Release(string tag, bool draft = false, bool preview = false, int day = 1, string[]? names = null) => new
    {
        tag_name = tag, draft, prerelease = preview, published_at = draft ? null : $"2026-09-{day:00}T00:00:00Z", body = "Release notes",
        assets = (names ?? []).Select((name, index) => new { id = index + 1, name, size = 3, digest = (string?)null }).ToArray()
    };
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("Availability failed: " + name); }
    private sealed class Releases : HttpMessageHandler
    {
        public object[] Items = [];
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            object payload = request.RequestUri!.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) ? Items : new { full_name = "owner/tool", description = "Tool", @private = false };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
}
