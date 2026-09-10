using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgramManager.Core;

public sealed class GitHubRepository
{
    public string FullName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Private { get; set; }
}

public sealed class GitHubSelection
{
    public string Repository { get; set; } = "";
    public string ModernAssetPattern { get; set; } = "";
    public string LegacyAssetPattern { get; set; } = "";
    public GitHubSelection Validate()
    {
        GitHubApi.RepositoryName(Repository);
        foreach (var pattern in new[] { ModernAssetPattern, LegacyAssetPattern })
        {
            CatalogRules.Text(pattern, 200);
            if (pattern.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new InvalidDataException("설치 파일 패턴은 경로 없이 파일 이름과 *, ?를 사용하세요.");
        }
        return this;
    }
}

public sealed class GitHubRelease
{
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset PublishedUtc { get; set; }
    public List<GitHubAsset> Assets { get; set; } = [];
}

public sealed class GitHubAsset
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class GitHubApi : IDisposable
{
    private readonly HttpClient http;
    private readonly string accessToken;
    private readonly object lifetimeLock = new();
    private int activeOperations;
    private bool disposeRequested;

    public GitHubApi(string token = "", HttpMessageHandler? handler = null)
    {
        if (token.Any(char.IsWhiteSpace)) throw new ArgumentException("GitHub 토큰에 공백을 넣을 수 없습니다.");
        accessToken = token;
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static string RepositoryName(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\\z") || value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 저장소는 소유자/저장소 형식으로 입력하세요.");
        return value;
    }

    public async Task<string> GetCurrentUserAsync(CancellationToken token = default)
    {
        using var operation = BeginOperation();
        using var json = await JsonAsync("user", token).ConfigureAwait(false);
        return Text(json.RootElement, "login");
    }

    public async Task<GitHubRepository> GetRepositoryAsync(string repository, CancellationToken token = default)
    {
        using var operation = BeginOperation();
        using var json = await JsonAsync("repos/" + RepositoryName(repository), token).ConfigureAwait(false);
        var item = json.RootElement;
        return new GitHubRepository { FullName = RepositoryName(Text(item, "full_name")), Description = Limited(Text(item, "description"), 10000), Private = item.TryGetProperty("private", out var privacy) && privacy.GetBoolean() };
    }

    public async Task<List<GitHubRepository>> ListRepositoriesAsync(string owner, CancellationToken token = default)
    {
        using var operation = BeginOperation();
        if (!Regex.IsMatch(owner ?? "", "\\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\\z")) throw new InvalidDataException("GitHub 계정 이름을 확인하세요.");
        var repos = new Dictionary<string, GitHubRepository>(StringComparer.OrdinalIgnoreCase);
        // /user/repos includes private repositories available to this token.
        var route = accessToken == "" ? "users/" + owner + "/repos?type=owner" : "user/repos?affiliation=owner,collaborator,organization_member";
        for (var page = 1; page <= 20; page++)
        {
            using var json = await JsonAsync(route + "&sort=updated&per_page=100&page=" + page, token).ConfigureAwait(false);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var fullName = RepositoryName(Text(item, "full_name"));
                if (!fullName.StartsWith(owner + "/", StringComparison.OrdinalIgnoreCase)) continue;
                repos[fullName] = new GitHubRepository { FullName = fullName, Description = Limited(Text(item, "description"), 10000), Private = item.TryGetProperty("private", out var privacy) && privacy.GetBoolean() };
            }
            if (json.RootElement.GetArrayLength() < 100) return repos.Values.OrderBy(r => r.FullName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        throw new InvalidDataException("접근 가능한 저장소가 2,000개를 넘습니다. 해당 계정의 저장소만 허용한 토큰을 사용하세요.");
    }

    public async Task<List<GitHubRelease>> GetReleasesAsync(string repository, CancellationToken token = default)
    {
        using var operation = BeginOperation();
        using var json = await JsonAsync("repos/" + RepositoryName(repository) + "/releases?per_page=100", token).ConfigureAwait(false);
        var releases = new List<GitHubRelease>();
        foreach (var item in json.RootElement.EnumerateArray())
        {
            if (item.GetProperty("draft").GetBoolean() || item.GetProperty("prerelease").GetBoolean()) continue;
            var tag = Text(item, "tag_name");
            var versionText = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
            string version;
            try { version = CatalogRules.NormalizeVersion(versionText); }
            catch (InvalidDataException) { continue; }
            var release = new GitHubRelease { Tag = tag, Version = version, Notes = Limited(Text(item, "body"), 30000), PublishedUtc = item.GetProperty("published_at").GetDateTimeOffset() };
            foreach (var asset in item.GetProperty("assets").EnumerateArray())
            {
                var name = Text(asset, "name");
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
                CatalogRules.InstallerName(name);
                var digest = Text(asset, "digest");
                var hash = "";
                if (digest != "")
                {
                    if (!Regex.IsMatch(digest, "\\Asha256:[a-fA-F0-9]{64}\\z")) throw new InvalidDataException(repository + ": GitHub 설치 파일의 SHA-256 형식이 올바르지 않습니다.");
                    hash = digest.Substring(7).ToUpperInvariant();
                }
                release.Assets.Add(new GitHubAsset { Id = asset.GetProperty("id").GetInt64(), Name = name, Size = asset.GetProperty("size").GetInt64(), Sha256 = hash });
            }
            releases.Add(release);
            if (releases.Count == 30) break;
        }
        return releases;
    }

    public async Task<string> GetReadmeAsync(string repository, CancellationToken token = default)
    {
        using var operation = BeginOperation();
        using var timeout = Deadline(token, TimeSpan.FromMinutes(2));
        using var response = await SendAsync(ApiUri("repos/" + RepositoryName(repository) + "/readme"), "application/vnd.github.raw+json", timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return "README가 없거나 읽을 수 없습니다. 아래 프로그램 정보와 변경 이력을 참고하세요.";
        EnsureSuccess(response);
        return new UTF8Encoding(false, true).GetString(await ReadBoundedAsync(response, 1024 * 1024, timeout.Token).ConfigureAwait(false));
    }

    public async Task DownloadAssetAsync(string repository, long assetId, Stream destination, long expectedSize, CancellationToken token = default)
    {
        using var operation = BeginOperation();
        if (assetId <= 0 || expectedSize <= 0 || expectedSize > CatalogRules.MaxPackageBytes) throw new InvalidDataException("GitHub 설치 파일 정보가 올바르지 않습니다.");
        using var timeout = Deadline(token, TimeSpan.FromMinutes(30));
        using var response = await SendAsync(ApiUri("repos/" + RepositoryName(repository) + "/releases/assets/" + assetId), "application/octet-stream", timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        if (response.Content.Headers.ContentLength is long length && length != expectedSize) throw new InvalidDataException("GitHub 설치 파일 크기가 변경되었습니다. 목록을 새로고침하세요.");
        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > expectedSize) throw new InvalidDataException("GitHub 설치 파일이 공지된 크기보다 큽니다.");
            await destination.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
        }
        if (total != expectedSize) throw new InvalidDataException("GitHub 설치 파일 다운로드가 완전하지 않습니다.");
    }

    private async Task<JsonDocument> JsonAsync(string route, CancellationToken token)
    {
        using var timeout = Deadline(token, TimeSpan.FromMinutes(2));
        using var response = await SendAsync(ApiUri(route), "application/vnd.github+json", timeout.Token).ConfigureAwait(false);
        EnsureSuccess(response);
        return JsonDocument.Parse(await ReadBoundedAsync(response, 8 * 1024 * 1024, timeout.Token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 32 });
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, string accept, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo != "" || !AllowedHost(uri.Host)) throw new InvalidDataException("GitHub 응답의 다운로드 주소가 허용되지 않습니다.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ProgramManager/0.2");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                if (accessToken != "") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("GitHub 다운로드 이동 주소가 없습니다.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("GitHub 다운로드 이동 횟수가 너무 많습니다.");
    }

    private static bool AllowedHost(string host) => host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    private static Uri ApiUri(string route) => new("https://api.github.com/" + route);
    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan timeout) { var source = CancellationTokenSource.CreateLinkedTokenSource(token); source.CancelAfter(timeout); return source; }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static string Limited(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum - 20) + "\n[긴 내용 일부 생략]";
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var hint = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "토큰 권한 또는 GitHub 요청 한도를 확인하세요." : response.StatusCode == HttpStatusCode.NotFound ? "저장소·릴리스·파일이 없거나 토큰에 읽기 권한이 없습니다." : "연결 상태를 확인한 뒤 다시 시도하세요.";
        throw new HttpRequestException("GitHub 요청 실패 (HTTP " + (int)response.StatusCode + "). " + hint);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("GitHub 응답이 허용 크기를 넘습니다.");
        using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("GitHub 응답이 허용 크기를 넘습니다.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    private Operation BeginOperation()
    {
        lock (lifetimeLock)
        {
            if (disposeRequested) throw new ObjectDisposedException(nameof(GitHubApi));
            activeOperations++;
            return new Operation(this);
        }
    }
    private void EndOperation()
    {
        bool close;
        lock (lifetimeLock) close = --activeOperations == 0 && disposeRequested;
        if (close) http.Dispose();
    }
    private sealed class Operation(GitHubApi owner) : IDisposable
    {
        private GitHubApi? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndOperation();
    }
    public void Dispose()
    {
        lock (lifetimeLock)
        {
            if (disposeRequested) return;
            disposeRequested = true;
            if (activeOperations != 0) return;
        }
        http.Dispose();
    }
}
