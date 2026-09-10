using System.Text.Json;
using System.Text.RegularExpressions;
using ProgramManager.Core;

namespace ProgramManager;

internal sealed class ManagerUpdate
{
    public CatalogApp App { get; set; } = new();
    public AppRelease Release { get; set; } = new();
    public PairingInfo? Host { get; set; }
    public string Source => Host is null ? "GitHub" : "호스트 " + Host.Host;

    public void RequireSame(Catalog catalog)
    {
        var app = catalog.Apps.SingleOrDefault(a => a.Id == App.Id);
        var release = app?.Releases.SingleOrDefault(r => r.Platform == Release.Platform && CatalogRules.Version(r.Version) == CatalogRules.Version(Release.Version));
        if (app?.GitHubRepository != App.GitHubRepository || release is null || release.FileName != Release.FileName
            || release.Size != Release.Size || release.Sha256 != Release.Sha256 || release.GitHubAssetId != Release.GitHubAssetId
            || release.GitHubTag != Release.GitHubTag || release.PublishedUtc != Release.PublishedUtc)
            throw new InvalidDataException("관리 프로그램 업데이트 파일이 변경되었습니다. 새 버전을 다시 확인하세요.");
    }
}

internal sealed class ManagerUpdater : IDisposable
{
    public const string Repository = "yunhyok/ProgramManager";
    private readonly GitHubApi api;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private DateTimeOffset checkedUtc;
    private bool disposed;
    public CatalogStore Store { get; }
    public string Downloads { get; }

    public ManagerUpdater(string root, GitHubApi? api = null)
    {
        this.api = api ?? new GitHubApi(); // Self updates are public; no client GitHub credentials are needed.
        Store = new CatalogStore(Path.Combine(root, "manager-updates")) { GitHub = this.api };
        Downloads = Path.Combine(root, "temp", "manager-updates");
    }

    public async Task RefreshAsync(bool force, CancellationToken token)
    {
        await refreshGate.WaitAsync(token);
        try
        {
            if (disposed) throw new ObjectDisposedException(nameof(ManagerUpdater));
            if (!force && checkedUtc != default && DateTimeOffset.UtcNow - checkedUtc < TimeSpan.FromHours(6)) return;
            var result = await Store.RefreshGitHubAsync([new GitHubSelection { Repository = Repository }], token);
            var failed = result.Checks.FirstOrDefault(c => c.App is null);
            if (failed != null) throw new InvalidOperationException(failed.Error);
            checkedUtc = DateTimeOffset.UtcNow;
        }
        finally { refreshGate.Release(); }
    }

    public async Task<ManagerUpdate?> CheckAsync(PairingInfo? host, string installedVersion, bool force, CancellationToken token)
    {
        Catalog catalog;
        if (host != null)
        {
            using var client = new CatalogClient(host, managerUpdates: true);
            catalog = await client.FetchCatalogAsync(token, forceManagerRefresh: force);
        }
        else { await RefreshAsync(force, token); catalog = Store.Read(); }
        return Select(catalog, installedVersion, Platforms.Current, host);
    }

    internal static ManagerUpdate? Select(Catalog catalog, string installedVersion, string platform, PairingInfo? host = null)
    {
        CatalogRules.Validate(catalog);
        CatalogRules.Platform(platform);
        var current = CatalogRules.Version(installedVersion);
        var app = catalog.Apps.SingleOrDefault(a => a.GitHubRepository.Equals(Repository, StringComparison.OrdinalIgnoreCase));
        var release = app?.Releases.Where(r => r.Platform == platform).OrderByDescending(r => CatalogRules.Version(r.Version)).FirstOrDefault();
        if (app is null || release is null || CatalogRules.Version(release.Version) <= current) return null;
        var name = Regex.Match(release.FileName, @"\AProgramManager-Setup-([0-9]+(?:\.[0-9]+){1,3})(-win7)?\.exe\z", RegexOptions.CultureInvariant);
        if (!name.Success || CatalogRules.Version(name.Groups[1].Value) != CatalogRules.Version(release.Version)
            || name.Groups[2].Success != (platform == Platforms.Legacy) || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("관리 프로그램 업데이트의 대상 Windows, 파일 이름 또는 SHA-256을 확인하지 못했습니다.");
        return new ManagerUpdate
        {
            App = JsonSerializer.Deserialize<CatalogApp>(JsonSerializer.SerializeToUtf8Bytes(app))!,
            Release = JsonSerializer.Deserialize<AppRelease>(JsonSerializer.SerializeToUtf8Bytes(release))!,
            Host = host is null ? null : PairingInfo.Parse(host.Export())
        };
    }

    internal static bool ShouldNotify(string version, string notifiedVersion) =>
        notifiedVersion.Length == 0 || CatalogRules.Version(version) > CatalogRules.Version(notifiedVersion);

    public async Task<string> DownloadAsync(ManagerUpdate update, IProgress<int>? progress, CancellationToken token)
    {
        if (update.Host != null)
        {
            using var client = new CatalogClient(update.Host, managerUpdates: true);
            update.RequireSame(await client.FetchCatalogAsync(token));
            return await client.DownloadAsync(update.App, update.Release, Downloads, progress, token);
        }
        await RefreshAsync(true, token);
        update.RequireSame(Store.Read());
        Directory.CreateDirectory(Downloads);
        var temporary = Path.Combine(Downloads, Guid.NewGuid().ToString("N") + ".part");
        var final = Path.ChangeExtension(temporary, ".exe");
        try
        {
            using (var package = await Store.PreparePackageAsync(update.App.Id, update.Release.Version, update.Release.Platform, token))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await package.Content.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                {
                    await output.WriteAsync(buffer, 0, read, token);
                    written += read;
                    progress?.Report((int)Math.Min(100, written * 100 / update.Release.Size));
                }
                if (written != update.Release.Size) throw new InvalidDataException("관리 프로그램 업데이트 파일 크기가 다릅니다.");
                await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, final);
            return final;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose() { disposed = true; api.Dispose(); }
}
