using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgramManager.Core;

public sealed class PreparedPackage : IDisposable
{
    private Action? releaseLease;
    public Stream Content { get; }
    public AppRelease Release { get; }
    internal PreparedPackage(Stream content, AppRelease release, Action? releaseLease = null) { Content = content; Release = release; this.releaseLease = releaseLease; }
    public void Dispose() { try { Content.Dispose(); } finally { Interlocked.Exchange(ref releaseLease, null)?.Invoke(); } }
}

public sealed partial class CatalogStore
{
    public GitHubApi? GitHub { get; set; }
    private readonly ConcurrentDictionary<string, SemaphoreSlim> cacheGates = new(StringComparer.Ordinal);
    private readonly object cacheLock = new();
    private readonly Dictionary<string, int> leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> downloads = new(StringComparer.OrdinalIgnoreCase);
    private string CacheRoot => Path.Combine(root, "temp");

    public async Task<Catalog> RefreshGitHubAsync(IEnumerable<GitHubSelection> selections, CancellationToken token = default)
    {
        var github = GitHub ?? throw new InvalidOperationException("호스트의 GitHub 연결을 먼저 설정하세요.");
        var selected = selections.ToList();
        if (selected.Count > 200 || selected.Any(s => s is null) || selected.Select(s => s.Validate().Repository).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidDataException("배포 저장소는 중복 없이 최대 200개까지 선택할 수 있습니다.");
        var apps = new List<CatalogApp>();
        foreach (var selection in selected)
        {
            var info = await github.GetRepositoryAsync(selection.Repository, token).ConfigureAwait(false);
            var releases = await github.GetReleasesAsync(selection.Repository, token).ConfigureAwait(false);
            var repoName = info.FullName.Split('/')[1];
            var slug = Regex.Replace(repoName.ToLowerInvariant(), "[^a-z0-9-]", "-");
            var app = new CatalogApp { Id = "gh-" + slug.Substring(0, Math.Min(slug.Length, 48)) + "-" + Key(info.FullName.ToLowerInvariant()).Substring(0, 8), Name = repoName, Description = info.Description, GitHubRepository = info.FullName };
            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in releases.OrderByDescending(r => CatalogRules.Version(r.Version)))
            {
                foreach (var platform in new[] { "win10-x64", "win7" })
                {
                    var pattern = platform == "win7" ? selection.LegacyAssetPattern : selection.ModernAssetPattern;
                    var asset = SelectAsset(release, platform, pattern, info.FullName);
                    if (asset is null || !versions.Add(release.Version + "/" + platform)) continue;
                    app.Releases.Add(new AppRelease { Platform = platform, Version = release.Version, Notes = release.Notes, FileName = asset.Name, Sha256 = asset.Sha256, Size = asset.Size, PublishedUtc = release.PublishedUtc, GitHubAssetId = asset.Id, GitHubTag = release.Tag });
                }
            }
            if (app.Releases.Count == 0) throw new InvalidDataException(info.FullName + ": 배포할 설치 파일이 없습니다. 정식 숫자 버전(v1.2.3)의 GitHub Release에 Setup/Install EXE 또는 MSI를 올리거나 파일 패턴을 지정하세요. ZIP은 지원하지 않습니다.");
            if (selection.ModernAssetPattern != "" && app.Releases.All(r => r.Platform != "win10-x64")) throw new InvalidDataException(info.FullName + ": Windows 10/11 파일 패턴과 일치하는 EXE/MSI가 없습니다.");
            if (selection.LegacyAssetPattern != "" && app.Releases.All(r => r.Platform != "win7")) throw new InvalidDataException(info.FullName + ": Windows 7 파일 패턴과 일치하는 EXE/MSI가 없습니다.");
            apps.Add(app);
        }
        token.ThrowIfCancellationRequested();
        using var writerLock = new FileStream(Path.Combine(root, ".publish.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var catalog = Read();
        catalog.Apps.RemoveAll(a => a.GitHubRepository != "");
        catalog.Apps.AddRange(apps);
        CatalogRules.Validate(catalog);
        JsonFiles.Write(CatalogPath, catalog);
        return catalog;
    }

    private static GitHubAsset? SelectAsset(GitHubRelease release, string platform, string pattern, string repository)
    {
        var assets = release.Assets.Where(a =>
        {
            if (pattern != "") return Regex.IsMatch(a.Name, "\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var name = a.Name.ToLowerInvariant();
            if (!name.Contains("setup") && !name.Contains("install") && !name.EndsWith(".msi", StringComparison.Ordinal)) return false;
            if (Regex.IsMatch(name, "(?:^|[-_.])(arm64|aarch64|arm)(?:[-_.]|$)")) return false;
            if (platform != "win7" && Regex.IsMatch(name, "(?:^|[-_.])(x86|win32)(?:[-_.]|$)")) return false;
            var legacy = Regex.IsMatch(name, "win(?:dows)?[-_.]?7|net[-_.]?4[._-]?[0-9]|legacy", RegexOptions.CultureInvariant);
            return platform == "win7" ? legacy : !legacy;
        }).ToList();
        if (assets.Count > 1) throw new InvalidDataException(repository + " " + release.Tag + " (" + platform + "): 설치 파일 후보가 여러 개입니다 (" + string.Join(", ", assets.Select(a => a.Name)) + "). 저장소 선택에서 파일 패턴을 지정하세요.");
        return assets.SingleOrDefault();
    }

    public async Task<PreparedPackage> PreparePackageAsync(string id, string version, string platform = "win10-x64", CancellationToken token = default)
    {
        var app = FindApp(id);
        var normalized = CatalogRules.NormalizeVersion(version);
        CatalogRules.Platform(platform);
        var release = app.Releases.SingleOrDefault(r => CatalogRules.Version(r.Version) == CatalogRules.Version(normalized) && r.Platform == platform) ?? throw new FileNotFoundException("버전이 없습니다.");
        if (app.GitHubRepository == "") return new PreparedPackage(new FileStream(GetPackagePath(id, version, platform), FileMode.Open, FileAccess.Read, FileShare.Read), release);
        var key = Key(app.GitHubRepository.ToLowerInvariant() + "/" + release.GitHubAssetId + "/" + release.Size + "/" + release.Sha256);
        var path = Path.Combine(CacheRoot, key + Path.GetExtension(release.FileName).ToLowerInvariant());
        var gate = cacheGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureCache();
            var hash = "";
            lock (cacheLock)
            {
                downloads.Add(path);
                if (File.Exists(path) && File.Exists(path + ".sha256"))
                {
                    RejectLink(path); RejectLink(path + ".sha256");
                    using (var cached = File.OpenRead(path)) hash = cached.Length == release.Size ? Compat.Hash(cached, token) : "";
                    var recorded = new FileInfo(path + ".sha256").Length == 64 ? File.ReadAllText(path + ".sha256") : "";
                    if (hash == "" || !hash.Equals(recorded, StringComparison.OrdinalIgnoreCase) || (release.Sha256 != "" && !hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (leases.ContainsKey(path)) throw new InvalidDataException("사용 중인 캐시 파일이 변경되었습니다. 전송 완료 후 다시 시도하세요.");
                        File.Delete(path); File.Delete(path + ".sha256"); hash = "";
                    }
                }
            }
            if (hash == "")
            {
                var temporary = path + ".part";
                try
                {
                    var github = GitHub ?? throw new InvalidOperationException("캐시에 없는 설치 파일을 받으려면 호스트에서 GitHub에 연결해야 합니다.");
                    RejectLink(temporary);
                    using (var output = new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, true))
                    {
                        await github.DownloadAssetAsync(app.GitHubRepository, release.GitHubAssetId, output, release.Size, token).ConfigureAwait(false);
                        await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); output.Position = 0;
                        hash = Compat.Hash(output, token);
                        if (release.Sha256 != "" && !hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub 설치 파일 SHA-256 검증에 실패했습니다.");
                    }
                    token.ThrowIfCancellationRequested();
                    lock (cacheLock)
                    {
                        RejectLink(path); RejectLink(path + ".sha256");
                        Compat.Replace(temporary, path);
                        File.WriteAllText(path + ".sha256", hash, new UTF8Encoding(false));
                    }
                }
                finally { lock (cacheLock) { if (File.Exists(temporary)) File.Delete(temporary); } }
            }
            lock (cacheLock)
            {
                release.Sha256 = hash;
                if (!leases.ContainsKey(path)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); File.SetLastWriteTimeUtc(path + ".sha256", DateTime.UtcNow); }
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                leases.TryGetValue(path, out var count); leases[path] = count + 1;
                return new PreparedPackage(stream, release, () => { lock (cacheLock) { if (--leases[path] == 0) leases.Remove(path); } });
            }
        }
        finally { lock (cacheLock) downloads.Remove(path); gate.Release(); }
    }

    public async Task<byte[]> FetchDocumentationAsync(string id, CancellationToken token = default)
    {
        var app = FindApp(id);
        var key = Key("docs/" + JsonSerializer.Serialize(app, JsonFiles.Options));
        var path = Path.Combine(CacheRoot, key + ".html");
        var temporary = path + ".part";
        var gate = cacheGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureCache();
            lock (cacheLock)
            {
                if (File.Exists(path))
                {
                    RejectLink(path);
                    if (new FileInfo(path).Length > CatalogRules.MaxDocumentationBytes) throw new InvalidDataException("캐시된 설명 파일이 너무 큽니다.");
                    var cached = File.ReadAllBytes(path); File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return cached;
                }
                downloads.Add(path);
            }
            var readme = app.GitHubRepository == "" ? app.Description : await (GitHub ?? throw new InvalidOperationException("설명 파일을 받으려면 호스트에서 GitHub에 연결해야 합니다.")).GetReadmeAsync(app.GitHubRepository, token).ConfigureAwait(false);
            var bytes = RenderDocumentation(app, readme);
            if (bytes.Length > CatalogRules.MaxDocumentationBytes) throw new InvalidDataException("설명 페이지가 허용 크기를 넘습니다.");
            token.ThrowIfCancellationRequested();
            lock (cacheLock)
            {
                RejectLink(path); RejectLink(temporary);
                File.WriteAllBytes(temporary, bytes); Compat.Replace(temporary, path);
            }
            return bytes;
        }
        finally { lock (cacheLock) { downloads.Remove(path); if (File.Exists(temporary)) File.Delete(temporary); } gate.Release(); }
    }

    public void CleanupCache(TimeSpan maxIdle)
    {
        if (maxIdle < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maxIdle));
        lock (cacheLock)
        {
            if (!Directory.Exists(CacheRoot)) return;
            RejectLink(CacheRoot);
            var cutoff = DateTime.UtcNow - maxIdle;
            foreach (var path in Directory.GetFiles(CacheRoot))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || File.GetLastWriteTimeUtc(path) > cutoff) continue;
                if (leases.Keys.Any(active => path == active || path.StartsWith(active + ".", StringComparison.OrdinalIgnoreCase)) || downloads.Any(active => path == active || path.StartsWith(active + ".", StringComparison.OrdinalIgnoreCase))) continue;
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    public void ClearCache() => CleanupCache(TimeSpan.Zero);
    private CatalogApp FindApp(string id) { CatalogRules.Id(id); return Read().Apps.SingleOrDefault(a => a.Id == id) ?? throw new FileNotFoundException("프로그램이 없습니다."); }
    private void EnsureCache() { lock (cacheLock) { Directory.CreateDirectory(CacheRoot); RejectLink(CacheRoot); } }
    private static void RejectLink(string path) { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("캐시 폴더와 파일에는 연결 경로를 사용할 수 없습니다."); }
    private static string Key(string value) => Compat.Hash(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static byte[] RenderDocumentation(CatalogApp app, string readme)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>");
        html.Append(WebUtility.HtmlEncode(app.Name)).Append(" — 프로그램 설명</title><style>body{box-sizing:border-box;font:16px/1.7 'Malgun Gothic',sans-serif;max-width:960px;margin:40px auto;padding:0 24px;color:#203047}h1,h2,h3{line-height:1.35}h2{margin-top:2em;border-bottom:1px solid #ccd5e0;padding-bottom:.4em}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f3f6fa;padding:16px;border-radius:6px}p{overflow-wrap:anywhere}.meta{color:#52647c}li{overflow-wrap:anywhere}</style></head><body><h1>").Append(WebUtility.HtmlEncode(app.Name)).Append("</h1><p class=\"meta\">호스트가 내려받은 오프라인 설명 · ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'")).Append("</p><p>").Append(WebUtility.HtmlEncode(app.Description)).Append("</p><p class=\"meta\">").Append(WebUtility.HtmlEncode(app.GitHubRepository)).Append("</p><h2>프로그램 설명 (README)</h2>");
        AppendMarkdownText(html, readme);
        html.Append("<h2>배포 버전과 변경 이력</h2>");
        foreach (var group in app.Releases.OrderByDescending(r => CatalogRules.Version(r.Version)).GroupBy(r => r.Version))
        {
            html.Append("<h3>").Append(WebUtility.HtmlEncode(group.Key)).Append("</h3><p class=\"meta\">").Append(WebUtility.HtmlEncode(string.Join(" · ", group.Select(r => r.Platform + " / " + r.FileName)))).Append("</p>");
            AppendMarkdownText(html, group.First().Notes);
        }
        html.Append("</body></html>");
        return new UTF8Encoding(false).GetBytes(html.ToString());
    }

    // Only headings, paragraphs and fenced text are rendered; raw HTML and links remain inert text.
    private static void AppendMarkdownText(StringBuilder html, string source)
    {
        var fenced = false;
        foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) { html.Append(fenced ? "</pre>" : "<pre>"); fenced = !fenced; continue; }
            if (fenced) { html.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var heading = Regex.Match(line, "^(#{1,6}) +(.+)$");
            if (heading.Success) { var level = Math.Min(6, heading.Groups[1].Length + 2); html.Append("<h").Append(level).Append('>').Append(WebUtility.HtmlEncode(heading.Groups[2].Value)).Append("</h").Append(level).Append('>'); }
            else html.Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>");
        }
        if (fenced) html.Append("</pre>");
    }
}
