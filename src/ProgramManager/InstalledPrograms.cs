using Microsoft.Win32;
using ProgramManager.Core;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ProgramManager;

internal sealed class InstalledPrograms
{
    internal sealed class Entry
    {
        public string Key = "", Name = "", Version = "", Location = "", Icon = "";
    }
    internal readonly List<Entry> Entries = [];
    internal readonly List<string> Shortcuts = [];

    public static InstalledPrograms Read()
    {
        var result = new InstalledPrograms();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry32, RegistryView.Registry64 } : new[] { RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null || key.GetValue("SystemComponent") is int hidden && hidden == 1) continue;
                        string Text(string field) => key.GetValue(field) as string ?? "";
                        if (Text("DisplayName").Length == 0) continue;
                        result.Entries.Add(new Entry { Key = hive + "|" + view + "|" + name, Name = Text("DisplayName"), Version = NumericVersion(Text("DisplayVersion")), Location = Text("InstallLocation"), Icon = Text("DisplayIcon") });
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            result.Shortcuts.AddRange(Files(Environment.GetFolderPath(folder), "*.lnk", 4));
        return result;
    }

    public LocalProgram? Find(CatalogApp app, LocalProgram? existing, out string reason)
    {
        var aliases = new[] { app.Name, app.GitHubRepository.Split('/').Last(), app.Id }.Select(Name).Where(n => n.Length > 0).ToArray();
        bool Matches(string text) => aliases.Contains(Name(text), StringComparer.Ordinal);
        var target = existing is null ? "" : Executable(existing.Path);
        var entries = Entries.Where(e => existing?.InstallationKey.Length > 0 ? e.Key == existing.InstallationKey : target.Length > 0
            ? SamePath(IconPath(e.Icon), target) || Matches(e.Name) && Inside(target, e.Location) : Matches(e.Name)).ToArray();
        var candidates = new List<LocalProgram>();
        foreach (var entry in entries)
        {
            var paths = new List<string>();
            if (target.Length > 0 && (SamePath(IconPath(entry.Icon), target) || Inside(target, entry.Location))) paths.Add(existing!.Path);
            if (paths.Count == 0)
            {
                paths.AddRange(Shortcuts.Where(p => Matches(Path.GetFileNameWithoutExtension(p))).Where(p => { var exe = Executable(p); return SamePath(exe, IconPath(entry.Icon)) || Inside(exe, entry.Location); }));
                if (paths.Count == 0 && Launchable(IconPath(entry.Icon))) paths.Add(IconPath(entry.Icon));
                if (paths.Count == 0)
                {
                    var files = Files(entry.Location, "*.exe", 1).Where(Launchable).ToArray();
                    var named = files.Where(p => Matches(Path.GetFileNameWithoutExtension(p))).ToArray();
                    if (named.Length > 0) paths.AddRange(named); else if (files.Length == 1) paths.Add(files[0]);
                }
            }
            foreach (var path in paths)
            {
                var exe = Executable(path);
                if (!Launchable(exe)) continue;
                var candidate = existing is null ? new LocalProgram { Name = app.Name } : AppState.Clone(existing);
                candidate.Path = path; candidate.InstallationKey = entry.Key;
                candidate.InstalledVersion = entry.Version.Length > 0 ? entry.Version : FileVersion(exe);
                candidates.Add(candidate);
            }
        }
        if (candidates.Count == 0 && entries.Length == 0)
        {
            var paths = target.Length > 0 ? new[] { existing!.Path } : Shortcuts.Where(p => Matches(Path.GetFileNameWithoutExtension(p)));
            foreach (var path in paths)
            {
                var exe = Executable(path);
                if (!Launchable(exe)) continue;
                var candidate = existing is null ? new LocalProgram { Name = app.Name } : AppState.Clone(existing);
                candidate.Path = path; candidate.InstalledVersion = FileVersion(exe);
                candidates.Add(candidate);
            }
        }
        var unique = candidates.GroupBy(p => Executable(p.Path) + "|" + p.InstalledVersion, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        reason = unique.Length == 0 ? "Windows 설치 기록이나 실행 파일에서 설치 결과를 확인하지 못했습니다." : unique.Length > 1 ? "실행 파일 후보가 여러 개입니다. 사용할 프로그램을 확인해 주세요." : "";
        return unique.Length == 1 ? unique[0] : null;
    }

    public static string Name(string value) => new(Regex.Replace(value ?? "", @"\s+[vV]?\d+(?:\.\d+){1,3}\s*$", "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    public static string NumericVersion(string value)
    {
        var match = Regex.Match(value.Trim(), @"\A[vV]?(\d+(?:\.\d+){1,3})(?=$|[+\-\s])");
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) && version > new Version(0, 0, 0, 0) ? match.Groups[1].Value : "";
    }
    public static string FileVersion(string path)
    {
        try { var info = FileVersionInfo.GetVersionInfo(path); var product = NumericVersion(info.ProductVersion ?? ""); return product.Length > 0 ? product : NumericVersion(info.FileVersion ?? ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }
    public static bool IsUpdate(LocalProgram program, AppRelease release) => !release.IsArchive && program.InstalledPlatform == Platforms.Current && release.Platform == Platforms.Current
        && Executable(program.Path).Length > 0 && Version.TryParse(program.InstalledVersion, out _)
        && Platforms.Numeric(program.InstalledVersion) < Platforms.Numeric(release.Version);
    public static bool ConfirmsInstall(LocalProgram? program, AppRelease release, int exitCode) => !release.IsArchive && (exitCode == 0 || exitCode == 3010) && release.Platform == Platforms.Current
        && program != null && Executable(program.Path).Length > 0 && Version.TryParse(program.InstalledVersion, out _)
        && Platforms.Numeric(program.InstalledVersion) >= Platforms.Numeric(release.Version);
    internal static string IconPath(string value)
    {
        var text = Environment.ExpandEnvironmentVariables(value.Trim());
        if (text.StartsWith("\"", StringComparison.Ordinal)) { var end = text.IndexOf('"', 1); return end > 1 ? text.Substring(1, end - 1) : ""; }
        return Regex.Replace(text, @",\s*-?\d+\s*$", "");
    }
    public static string Executable(string path)
    {
        if (!File.Exists(path)) return "";
        if (Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return "";
        object? shell = null, shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            shortcut = shell!.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            var target = shortcut!.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string ?? "";
            return Launchable(target) ? target : "";
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or ArgumentException) { return ""; }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); if (shell != null) Marshal.FinalReleaseComObject(shell); }
    }
    private static bool Launchable(string path) => File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        && !Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"\A(?:unins|uninstall|setup|updater|update|crash|maintenance|(?:cmd|powershell|pwsh|pythonw?|wscript|cscript|rundll32|msiexec|explorer|dotnet)$)", RegexOptions.IgnoreCase);
    private static bool SamePath(string left, string right) => left.Length > 0 && right.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string file, string folder)
    {
        try { return Path.IsPathRooted(folder) && Path.IsPathRooted(file) && Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static IEnumerable<string> Files(string folder, string pattern, int depth)
    {
        if (!Directory.Exists(folder)) yield break;
        string[] files, directories;
        try
        {
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) yield break;
            files = Directory.GetFiles(folder, pattern).Take(2000).ToArray();
            directories = depth > 0 ? Directory.GetDirectories(folder).Take(200).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var file in files) yield return file;
        foreach (var directory in directories) foreach (var file in Files(directory, pattern, depth - 1)) yield return file;
    }
}

internal static class ArchiveDownloads
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsFolder()
    {
        var id = DownloadsFolderId;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            return Path.GetFullPath(Marshal.PtrToStringUni(pointer) ?? throw new IOException("Windows 다운로드 폴더를 찾지 못했습니다."));
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    public static string MoveVerified(string source, string originalFileName, string? downloadsFolder = null)
    {
        source = Path.GetFullPath(source);
        CatalogRules.PackageName(originalFileName);
        if (!Path.GetExtension(originalFileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP 파일 이름이 올바르지 않습니다.");
        var directory = Path.GetFullPath(downloadsFolder ?? DownloadsFolder());
        Directory.CreateDirectory(directory);
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var extension = Path.GetExtension(originalFileName);
        for (var suffix = 0; ; suffix++)
        {
            var destination = Path.Combine(directory, suffix == 0 ? originalFileName : $"{stem} ({suffix}){extension}");
            try { File.Move(source, destination); return destination; }
            catch (IOException) when (File.Exists(source) && File.Exists(destination)) { }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
