using Microsoft.Win32;
using ProgramManager.Core;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProgramManager;

public sealed class LocalProgram
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string HostFingerprint { get; set; } = "";
    public string InstalledPlatform { get; set; } = "";
}

public sealed class UserSettings
{
    public bool HostEnabled { get; set; }
    public int Port { get; set; } = 45672;
    public string AdvertisedHost { get; set; } = Environment.MachineName;
    public bool AutoStart { get; set; }
    public string PairingProtected { get; set; } = "";
    public List<LocalProgram> Programs { get; set; } = [];
    public string GitHubOwner { get; set; } = "";
    public bool UseGitHubCli { get; set; } = true;
    public string GitHubTokenProtected { get; set; } = "";
    public int CacheRetentionDays { get; set; } = 7;
    public List<GitHubSelection> GitHubRepositories { get; set; } = [];
}

public sealed class CachedCatalog
{
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CheckedUtc { get; set; }
    public Catalog Catalog { get; set; } = new();
}

public sealed class AppState
{
    public string Root { get; }
    public UserSettings Settings { get; private set; }
    public CachedCatalog Cache { get; set; }
    public CatalogStore Store { get; }
    public string Downloads => Path.Combine(Root, "temp", "installers");
    public string Documents => Path.Combine(Root, "temp", "docs");

    public AppState(string root)
    {
        Root = Path.GetFullPath(root);
        Settings = JsonFiles.Read(Path.Combine(root, "settings.json"), new UserSettings());
        if (!File.Exists(Path.Combine(root, "settings.json")))
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            Settings.AutoStart = run?.GetValue("ProgramManager") is string;
        }
        ValidateSettings(Settings);
        Cache = JsonFiles.Read(Path.Combine(root, "cache.json"), new CachedCatalog());
        ValidateCache(Cache);
        Store = new CatalogStore(Path.Combine(root, "repository"));
    }

    public void Save() { ValidateSettings(Settings); JsonFiles.Write(Path.Combine(Root, "settings.json"), Settings); }
    public void SaveCache() { ValidateCache(Cache); JsonFiles.Write(Path.Combine(Root, "cache.json"), Cache); }
    public PairingInfo? Pairing => string.IsNullOrWhiteSpace(Settings.PairingProtected) ? null : PairingInfo.Parse(Secrets.Unprotect(Settings.PairingProtected));
    public LocalProgram? FindInstalled(string id, string fingerprint) => Settings.Programs.FirstOrDefault(p => p.CatalogId == id && p.HostFingerprint == fingerprint);

    public LocalProgram SaveProgram(LocalProgram item)
    {
        if (item is null) throw new InvalidDataException("등록할 프로그램 정보가 없습니다.");
        ValidateProgram(item);
        ValidateLaunchPath(item.Path);
        var staged = Clone(Settings);
        var saved = Clone(item);
        var source = Path.GetFullPath(item.Path);
        var existing = staged.Programs.FirstOrDefault(p => p.Id == saved.Id)
            ?? staged.Programs.FirstOrDefault(p => SamePath(p.Path, source) || (p.SourcePath.Length > 0 && SamePath(p.SourcePath, source)));
        if (existing is not null) saved.Id = existing.Id;
        saved.Path = source;
        string? ownedShortcut = null;
        try
        {
            if (Path.GetExtension(source).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcuts = Path.Combine(Root, "shortcuts");
                Directory.CreateDirectory(shortcuts);
                var alreadyOwned = Path.GetDirectoryName(source)!.Equals(shortcuts, StringComparison.OrdinalIgnoreCase)
                    && staged.Programs.Any(p => SamePath(p.Path, source));
                if (alreadyOwned) saved.SourcePath = existing?.SourcePath ?? "";
                else
                {
                    ownedShortcut = Path.Combine(shortcuts, Guid.NewGuid().ToString("N") + ".lnk");
                    File.Copy(source, ownedShortcut, false);
                    saved.Path = ownedShortcut;
                    saved.SourcePath = source;
                }
            }
            else saved.SourcePath = "";
            staged.Programs.RemoveAll(p => p.Id == saved.Id);
            staged.Programs.Add(saved);
            CommitSettings(staged);
            return Settings.Programs.Single(p => p.Id == saved.Id);
        }
        catch
        {
            if (ownedShortcut is not null && File.Exists(ownedShortcut)) File.Delete(ownedShortcut);
            throw;
        }
    }

    public void CommitSettings(UserSettings next)
    {
        if (next is null) throw new InvalidDataException("저장할 설정이 없습니다.");
        var staged = Clone(next);
        ValidateSettings(staged);
        if (staged.AutoStart == Settings.AutoStart)
        {
            JsonFiles.Write(Path.Combine(Root, "settings.json"), staged);
            Settings = staged;
            return;
        }
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new IOException("Windows 시작 프로그램 설정을 열 수 없습니다.");
        var existed = run.GetValueNames().Contains("ProgramManager", StringComparer.OrdinalIgnoreCase);
        var previous = existed ? run.GetValue("ProgramManager", null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
        var kind = existed ? run.GetValueKind("ProgramManager") : RegistryValueKind.String;
        try
        {
            if (staged.AutoStart) run.SetValue("ProgramManager", $"\"{Application.ExecutablePath}\" --tray", RegistryValueKind.String);
            else run.DeleteValue("ProgramManager", false);
            JsonFiles.Write(Path.Combine(Root, "settings.json"), staged);
        }
        catch (Exception saveError)
        {
            try
            {
                if (existed) run.SetValue("ProgramManager", previous!, kind);
                else run.DeleteValue("ProgramManager", false);
            }
            catch (Exception rollbackError) { throw new AggregateException("설정 저장과 시작 프로그램 복원에 실패했습니다. Windows 시작 프로그램 설정을 확인하세요.", saveError, rollbackError); }
            throw;
        }
        Settings = staged;
    }

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, JsonFiles.Options), JsonFiles.Options)!;
    private static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static void ValidateSettings(UserSettings settings)
    {
        try
        {
            if (settings is null || settings.Programs is null || settings.Programs.Count > 1000 || settings.Programs.Any(p => p is null)) throw new InvalidDataException("프로그램 목록이 비어 있거나 너무 큽니다.");
            new PairingInfo { Host = settings.AdvertisedHost, Port = settings.Port, Fingerprint = new string('0', 64), Token = new string('0', 64) }.Validate();
            CatalogRules.Text(settings.PairingProtected, 20000);
            CatalogRules.Text(settings.GitHubOwner, 100);
            if (settings.GitHubOwner.Length > 0 && !Regex.IsMatch(settings.GitHubOwner, "\\A[a-zA-Z0-9-]{1,39}\\z")) throw new InvalidDataException("GitHub 계정 이름을 확인하세요.");
            CatalogRules.Text(settings.GitHubTokenProtected, 20000);
            if (settings.CacheRetentionDays is < 1 or > 30 || settings.GitHubRepositories is null || settings.GitHubRepositories.Count > 200) throw new InvalidDataException("캐시 보관 기간 또는 GitHub 저장소 목록이 올바르지 않습니다.");
            foreach (var source in settings.GitHubRepositories) (source ?? throw new InvalidDataException("저장소 정보가 비어 있습니다.")).Validate();
            if (settings.PairingProtected.Length > 0) PairingInfo.Parse(Secrets.Unprotect(settings.PairingProtected));
            foreach (var item in settings.Programs) ValidateProgram(item);
            if (settings.Programs.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != settings.Programs.Count) throw new InvalidDataException("프로그램 ID가 중복됩니다.");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or FormatException or System.Security.Cryptography.CryptographicException)
        { throw new InvalidDataException("settings.json 설정이 올바르지 않습니다. 원본 파일을 보존한 뒤 프로그램 정보 또는 연결 코드를 수정하세요. " + ex.Message, ex); }
    }

    private static void ValidateProgram(LocalProgram item)
    {
        if (item is null || item.Id is null || !Guid.TryParse(item.Id, out _)) throw new InvalidDataException("프로그램 ID가 올바르지 않습니다.");
        CatalogRules.Text(item.Name, 200, true);
        CatalogRules.Text(item.Path, 32767, true);
        CatalogRules.Text(item.SourcePath, 32767);
        CatalogRules.Text(item.InstalledVersion, 43);
        CatalogRules.Text(item.CatalogId, 64);
        CatalogRules.Text(item.HostFingerprint, 64);
        CatalogRules.Text(item.InstalledPlatform, 20);
        if (!Path.IsPathRooted(item.Path) || !new[] { ".exe", ".lnk" }.Contains(Path.GetExtension(item.Path), StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("프로그램 경로는 EXE 또는 LNK 파일의 전체 경로여야 합니다.");
        Path.GetFullPath(item.Path);
        if (item.SourcePath.Length > 0 && !Path.IsPathRooted(item.SourcePath)) throw new InvalidDataException("바로가기 원본 경로가 올바르지 않습니다.");
        if (item.InstalledVersion.Length > 0) CatalogRules.Version(item.InstalledVersion);
        if (item.CatalogId.Length > 0) CatalogRules.Id(item.CatalogId);
        if (item.HostFingerprint.Length > 0 && !Regex.IsMatch(item.HostFingerprint, "\\A[a-fA-F0-9]{64}\\z")) throw new InvalidDataException("호스트 인증서 정보가 올바르지 않습니다.");
        if ((item.CatalogId.Length == 0) != (item.HostFingerprint.Length == 0)) throw new InvalidDataException("배포 프로그램 ID와 호스트 인증서 정보를 함께 지정해야 합니다.");
        if (item.InstalledPlatform.Length > 0) CatalogRules.Platform(item.InstalledPlatform);
    }

    private static void ValidateCache(CachedCatalog cache)
    {
        try
        {
            if (cache is null) throw new InvalidDataException("캐시 정보가 비어 있습니다.");
            CatalogRules.Text(cache.Fingerprint, 64);
            if (cache.Fingerprint.Length > 0 && !Regex.IsMatch(cache.Fingerprint, "\\A[a-fA-F0-9]{64}\\z")) throw new InvalidDataException("호스트 인증서 정보가 올바르지 않습니다.");
            CatalogRules.Validate(cache.Catalog);
            if (cache.Catalog.Apps.Count > 0 && (cache.Fingerprint.Length == 0 || cache.CheckedUtc == default)) throw new InvalidDataException("카탈로그의 호스트 또는 확인 시간이 없습니다.");
        }
        catch (InvalidDataException ex) { throw new InvalidDataException("cache.json 캐시가 올바르지 않습니다. 원본 파일을 보존하고 호스트에서 카탈로그를 다시 받으세요. " + ex.Message, ex); }
    }

    public static void ValidateLaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) || !File.Exists(path) || !new[] { ".exe", ".lnk" }.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("존재하는 실행 파일(.exe) 또는 바로가기(.lnk)를 선택하세요.");
    }

    public static void Launch(LocalProgram item)
    {
        ValidateLaunchPath(item.Path);
        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(item.Path)! });
    }

}

internal static class Platforms
{
    public const string Modern = "win10-x64";
    public const string Legacy = "win7";
    public static string Current => Environment.OSVersion.Version.Major >= 10 && Environment.Is64BitOperatingSystem ? Modern : Legacy;
    public static string Label(string platform) => platform == Modern ? "Windows 10/11 (64비트)" : "Windows 7/8 (32·64비트)";
    public static Version Numeric(string version)
    {
        var value = Version.Parse(version);
        return new Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    }
    public static AppRelease? Latest(CatalogApp app, string platform) => app.Releases.Where(r => r.Platform == platform).OrderByDescending(r => Numeric(r.Version)).FirstOrDefault();
}
