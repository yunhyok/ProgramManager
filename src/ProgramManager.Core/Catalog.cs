using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ProgramManager.Core;

public sealed class Catalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<CatalogApp> Apps { get; set; } = [];
}

public sealed class CatalogApp
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string GitHubRepository { get; set; } = "";
    public List<AppRelease> Releases { get; set; } = [];
    [JsonIgnore]
    public AppRelease? Latest => Releases.OrderByDescending(r => CatalogRules.Version(r.Version)).FirstOrDefault();
}

public sealed class AppRelease
{
    public string Platform { get; set; } = "win10-x64";
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset PublishedUtc { get; set; }
    public long GitHubAssetId { get; set; }
    public string GitHubTag { get; set; } = "";
}

public static class CatalogRules
{
    public const int MaxCatalogBytes = 4 * 1024 * 1024;
    public const int MaxDocumentationBytes = 4 * 1024 * 1024;
    public const long MaxPackageBytes = 8L * 1024 * 1024 * 1024;

    public static string Platform(string value)
    {
        if (value != "win10-x64" && value != "win7") throw new InvalidDataException("지원하는 플랫폼은 win10-x64 또는 win7입니다.");
        return value;
    }

    public static string Id(string value)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("프로그램 ID는 영문 소문자, 숫자, 하이픈으로 1~64자여야 합니다.");
        return value;
    }

    public static Version Version(string value)
    {
        if (value is null || value.Length > 43 || !Regex.IsMatch(value, "\\A[0-9]+(?:\\.[0-9]+){1,3}\\z") || !System.Version.TryParse(value, out var parsed))
            throw new InvalidDataException("버전은 1.2, 1.2.3 또는 1.2.3.4 형식이어야 합니다.");
        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }

    public static string NormalizeVersion(string value)
    {
        var version = Version(value);
        return version.ToString(version.Revision != 0 ? 4 : version.Build != 0 ? 3 : 2);
    }

    public static string InstallerName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 240 || value != Path.GetFileName(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("설치 파일 이름이 올바르지 않습니다.");
        var extension = Path.GetExtension(value);
        if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EXE 또는 MSI 설치 파일만 지원합니다.");
        return value;
    }

    public static Catalog Validate(Catalog catalog)
    {
        if (catalog is null || catalog.SchemaVersion != 1 || catalog.Apps is null || catalog.Apps.Count > 200)
            throw new InvalidDataException("지원하지 않거나 너무 큰 카탈로그입니다.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in catalog.Apps)
        {
            if (app is null || !ids.Add(Id(app.Id))) throw new InvalidDataException("중복 또는 빈 프로그램 정보입니다.");
            Text(app.Name, 200, true); Text(app.Description, 10000);
            if (app.GitHubRepository != "") GitHubApi.RepositoryName(app.GitHubRepository);
            if (app.Releases is null || app.Releases.Count is < 1 or > 500) throw new InvalidDataException("버전 목록이 올바르지 않습니다.");
            var versions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var release in app.Releases)
            {
                if (release is null || !versions.Add(NormalizeVersion(release.Version) + "/" + Platform(release.Platform))) throw new InvalidDataException("중복 또는 빈 버전 정보입니다.");
                Text(release.Notes, 30000);
                InstallerName(release.FileName);
                Text(release.GitHubTag, 200);
                var github = app.GitHubRepository != "" && release.GitHubAssetId > 0 && release.GitHubTag != "";
                if ((app.GitHubRepository != "") != github || (app.GitHubRepository == "" && (release.GitHubAssetId != 0 || release.GitHubTag != "")))
                    throw new InvalidDataException("GitHub 배포 파일 식별 정보가 올바르지 않습니다.");
                if (release.Sha256 is null || !(Regex.IsMatch(release.Sha256, "\\A[a-fA-F0-9]{64}\\z") || (github && release.Sha256 == "")) || release.Size is <= 0 or > MaxPackageBytes || release.PublishedUtc == default)
                    throw new InvalidDataException("설치 파일의 크기, 해시 또는 게시 날짜가 올바르지 않습니다.");
            }
        }
        return catalog;
    }

    public static void Text(string text, int maximum, bool required = false)
    {
        if (text is null || text.Length > maximum || (required && string.IsNullOrWhiteSpace(text)) || text.Any(c => char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
            throw new InvalidDataException("텍스트가 비어 있거나 허용 길이 또는 문자 범위를 벗어났습니다.");
    }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, MaxDepth = 32 };

    public static T Read<T>(string path, T fallback)
    {
        if (!File.Exists(path)) return fallback;
        using var stream = File.OpenRead(path);
        if (stream.Length > CatalogRules.MaxCatalogBytes) throw new InvalidDataException("JSON 파일이 너무 큽니다.");
        return JsonSerializer.Deserialize<T>(stream, Options) ?? throw new InvalidDataException("JSON 파일이 비어 있습니다.");
    }

    public static void Write<T>(string path, T value)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > CatalogRules.MaxCatalogBytes) throw new InvalidDataException("JSON 파일이 너무 큽니다.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            Compat.Replace(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
