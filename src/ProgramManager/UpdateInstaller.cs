using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using ProgramManager.Core;

namespace ProgramManager;

public sealed class UpdateInstallationResult
{
    public string Status { get; internal set; } = "";
    public string Version { get; internal set; } = "";
    public string LogPath { get; internal set; } = "";
    public string Message { get; internal set; } = "";
}

/// <summary>Hands a verified update to Inno Setup, which waits for this app to exit.</summary>
public static class UpdateInstaller
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{430AD44C-84B5-44AB-B870-0717B39671C9}_is1";
    public const string ResultFileName = "update-result.ini";
    public const string LogFileName = "update-install.log";

    public static bool IsInstalledApplication(string installDirectory)
    {
        try
        {
            var directory = AbsolutePath(installDirectory);
            if (!File.Exists(Path.Combine(directory, "ProgramManager.exe"))) return false;
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                if (view == RegistryView.Registry64 && !Environment.Is64BitOperatingSystem) continue;
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = hive.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string registered &&
                    string.Equals(directory, AbsolutePath(registered), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return false;
    }

    public static ProcessStartInfo CreateStartInfo(string verifiedInstallerPath, string expectedSha256,
        string expectedVersion, string installDirectory, string dataDirectory, int currentProcessId)
    {
        using var file = OpenInstaller(verifiedInstallerPath);
        return ValidateAndCreate(file, expectedSha256, expectedVersion, installDirectory, dataDirectory, currentProcessId);
    }

    public static void Start(string verifiedInstallerPath, string expectedSha256, string expectedVersion,
        string installDirectory, string dataDirectory, int currentProcessId)
    {
        // Keep the file open without write/delete sharing through Process.Start to prevent replacement after hashing.
        using var file = OpenInstaller(verifiedInstallerPath);
        var info = ValidateAndCreate(file, expectedSha256, expectedVersion, installDirectory, dataDirectory, currentProcessId);
        var directory = AbsolutePath(dataDirectory);
        WritePendingResult(directory, expectedVersion);
        using var process = Process.Start(info) ?? throw new IOException("업데이트 설치 프로그램을 시작하지 못했습니다.");
    }

    private static FileStream OpenInstaller(string path) =>
        new(AbsolutePath(path), FileMode.Open, FileAccess.Read, FileShare.Read);

    private static ProcessStartInfo ValidateAndCreate(FileStream file, string hash, string version,
        string installDirectory, string dataDirectory, int pid)
    {
        var install = AbsolutePath(installDirectory);
        var data = AbsolutePath(dataDirectory);
        if (!IsInstalledApplication(install))
            throw new InvalidOperationException("설치 프로그램으로 설치한 Program Manager에서 업데이트를 실행하세요.");
        if (!Directory.Exists(data)) throw new DirectoryNotFoundException("기존 데이터 폴더를 찾을 수 없습니다.");
        if (string.Equals(install, data, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("자동 업데이트를 사용하려면 데이터 폴더와 프로그램 설치 폴더를 구분해 주세요.");
        ValidateProcessImage(pid, Path.Combine(install, "ProgramManager.exe"));
        ValidateInstaller(file, hash, version);
        if (NormalizeVersion(version) < new Version(0, 3, 0, 0))
            throw new InvalidOperationException("이 설치 파일은 자동 업데이트를 지원하지 않습니다. 0.3.0 이상이 필요합니다.");
        return BuildStartInfo(file.Name, install, data, pid);
    }

    internal static void ValidateInstaller(FileStream file, string expectedHash, string expectedVersion)
    {
        if (expectedHash == null || expectedHash.Length != 64 || expectedHash.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("설치 파일의 SHA-256 정보가 올바르지 않습니다.");
        file.Position = 0;
        using var algorithm = SHA256.Create();
        var actual = BitConverter.ToString(algorithm.ComputeHash(file)).Replace("-", "");
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("설치 파일 검증에 실패했습니다. 업데이트 파일을 다시 다운로드하세요.");
        var metadata = FileVersionInfo.GetVersionInfo(file.Name);
        if (!string.Equals(metadata.ProductName?.Trim(), "Program Manager", StringComparison.Ordinal) ||
            !string.Equals(metadata.FileDescription?.Trim(), "Program Manager Setup", StringComparison.Ordinal) ||
            !string.Equals(metadata.CompanyName?.Trim(), "yunhyok", StringComparison.Ordinal) ||
            NormalizeVersion(metadata.ProductVersion?.Trim() ?? "") != NormalizeVersion(expectedVersion))
            throw new InvalidDataException("Program Manager 설치 파일의 제품 또는 버전 정보가 일치하지 않습니다.");
    }

    internal static Version NormalizeVersion(string version)
    {
        if (!Version.TryParse(version, out var result) || result.Major < 0 || result.Minor < 0)
            throw new InvalidDataException("업데이트 버전 정보가 올바르지 않습니다.");
        return new Version(result.Major, result.Minor, Math.Max(0, result.Build), Math.Max(0, result.Revision));
    }

    internal static ProcessStartInfo BuildStartInfo(string file, string install, string data, int pid)
    {
        file = AbsolutePath(file);
        install = AbsolutePath(install);
        data = AbsolutePath(data);
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        return new ProcessStartInfo
        {
            FileName = file,
            Arguments = "/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /PMUPDATE /PMPID=" +
                pid.ToString(CultureInfo.InvariantCulture) + " /PMDATA=" + Quote(data) + " /DIR=" + Quote(install) +
                " /LOG=" + Quote(Path.Combine(data, LogFileName)),
            WorkingDirectory = Path.GetDirectoryName(file)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
    }

    // Quotes/control characters are rejected before quoting. A root directory ends with '\', so use '\.'
    // to avoid the different trailing-backslash rules used by Windows argument parsers.
    private static string Quote(string value) => "\"" + (value.EndsWith("\\", StringComparison.Ordinal) ? value + "." : value) + "\"";

    internal static string AbsolutePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || c == '"' || c == '*' || c == '?') ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !(value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/') ||
              value.StartsWith(@"\\", StringComparison.Ordinal)))
            throw new ArgumentException("업데이트 경로는 올바른 절대 경로여야 합니다.");
        var full = Path.GetFullPath(value);
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
    }

    internal static void ValidateProcessImage(int pid, string expectedPath)
    {
        if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION, supported by Windows 7.
        var image = new StringBuilder(32768);
        var length = image.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, image, ref length) ||
            !string.Equals(AbsolutePath(image.ToString()), AbsolutePath(expectedPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트를 요청한 Program Manager 실행 경로를 확인할 수 없습니다.");
    }

    private static void WritePendingResult(string directory, string version)
    {
        var path = Path.Combine(directory, ResultFileName);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, "[Update]\r\nStatus=pending\r\nVersion=" + NormalizeVersion(version).ToString(3) +
                "\r\nLogPath=" + Path.Combine(directory, LogFileName) + "\r\nMessage=업데이트 설치가 시작되었습니다.\r\n", new UTF8Encoding(true));
            Compat.Replace(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static UpdateInstallationResult? TakeResult(string dataDirectory)
    {
        var directory = AbsolutePath(dataDirectory);
        var path = Path.Combine(directory, ResultFileName);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("업데이트 결과 파일이 너무 큽니다.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator > 0) values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
        }
        string Value(string name) => values.TryGetValue(name, out var text) ? text : "";
        var result = new UpdateInstallationResult { Status = Value("Status"), Version = Value("Version"),
            LogPath = Path.Combine(directory, LogFileName), Message = Value("Message") };
        if (result.Status != "pending" && result.Status != "succeeded" && result.Status != "failed")
        { result.Status = "failed"; result.Message = "업데이트 완료 여부를 확인할 수 없습니다. 설치 로그를 확인하세요."; }
        var previous = Path.Combine(directory, "last-update-result.ini");
        Compat.Replace(path, previous);
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder fileName, ref int size);
}
