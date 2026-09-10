using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ProgramManager;

internal static class UpdateInstallerChecks
{
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "update 한글 & spaces");
        Directory.CreateDirectory(folder);
        var installer = Path.Combine(folder, "download-guid.exe");
        var install = Path.Combine(folder, "Program Manager & 도구");
        var data = Path.Combine(folder, "원래 데이터");
        var info = UpdateInstaller.BuildStartInfo(installer, install, data, 2345);
        Assert(info.FileName == installer && info.WorkingDirectory == folder && !info.UseShellExecute &&
            info.CreateNoWindow && info.WindowStyle == ProcessWindowStyle.Hidden, "installer executes directly without a shell");
        var arguments = SplitArguments(info.Arguments);
        Assert(arguments.Contains("/PMUPDATE") && arguments.Contains("/NOCLOSEAPPLICATIONS") &&
            arguments.Contains("/NORESTART") && arguments.Contains("/PMPID=2345") &&
            arguments.Contains("/PMDATA=" + data) && arguments.Contains("/DIR=" + install) &&
            arguments.Contains("/LOG=" + Path.Combine(data, UpdateInstaller.LogFileName)), "spaces, Unicode and ampersands round-trip as one argument");
        var rootPath = Path.GetPathRoot(folder)!;
        var rootArguments = SplitArguments(UpdateInstaller.BuildStartInfo(installer, install, rootPath, 1).Arguments);
        Assert(rootArguments.Contains("/PMDATA=" + rootPath + "."), "root data directory avoids a trailing-backslash quote ambiguity");
        foreach (var invalid in new[] { "relative", "C:relative", @"\rooted", data + "\" /DIR=bad", data + "\r\n/PMUPDATE", @"\\?\C:\device", @"\\.\C:\device" })
            Reject(() => UpdateInstaller.BuildStartInfo(installer, install, invalid, 1), "unsafe path rejected");
        Reject(() => UpdateInstaller.BuildStartInfo(installer, install, data, 0), "invalid PID rejected");
        Assert(!UpdateInstaller.IsInstalledApplication(folder), "development/unregistered directory cannot update installed app");
        Assert(UpdateInstaller.NormalizeVersion("0.3") == UpdateInstaller.NormalizeVersion("0.3.0.0"), "version normalization");

        using var current = Process.GetCurrentProcess();
        var currentImage = current.MainModule!.FileName!;
        UpdateInstaller.ValidateProcessImage(current.Id, currentImage);
        Reject(() => UpdateInstaller.ValidateProcessImage(current.Id, Path.Combine(folder, "ProgramManager.exe")), "live wrong-image PID rejected");
        Reject(() => UpdateInstaller.ValidateProcessImage(int.MaxValue, currentImage), "missing PID rejected before handoff");
        using (var image = new FileStream(currentImage, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => UpdateInstaller.ValidateInstaller(image, Hash(image), "0.3.0"), "ordinary executable is not an installer even with a matching hash");

        // Exercise a real Inno executable when release artifacts are available; never execute it.
        var repository = FindRepository();
        var existing = repository == null ? null : Path.Combine(repository, "artifacts", "installers", "ProgramManager-Setup-0.2.2-win7.exe");
        if (existing != null && File.Exists(existing))
        {
            File.Copy(existing, installer);
            using var image = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Hash(image);
            UpdateInstaller.ValidateInstaller(image, hash.ToLowerInvariant(), "0.2.2");
            Reject(() => UpdateInstaller.ValidateInstaller(image, new string('0', 64), "0.2.2"), "corrupt digest rejected");
            Reject(() => UpdateInstaller.ValidateInstaller(image, hash, "0.3.0"), "different product version rejected");
            Reject(() => UpdateInstaller.ValidateInstaller(image, "not-a-hash", "0.2.2"), "invalid digest metadata rejected");
            Reject(() => new FileStream(installer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose(), "verification lease prevents file mutation");
        }
        else Console.WriteLine("SKIP: existing 0.2.2 Inno artifact is not available for resource verification");

        Directory.CreateDirectory(data);
        var resultPath = Path.Combine(data, UpdateInstaller.ResultFileName);
        Assert(UpdateInstaller.TakeResult(data) == null, "no result is a normal startup");
        foreach (var status in new[] { "pending", "failed", "succeeded" })
        {
            File.WriteAllText(resultPath, "[Update]\r\nStatus=" + status + "\r\nVersion=0.3.0\r\nLogPath=untrusted-other-file\r\nMessage=한글 결과\r\n", new UTF8Encoding(true));
            var result = UpdateInstaller.TakeResult(data)!;
            Assert(result.Status == status && result.Version == "0.3.0" && result.Message == "한글 결과" &&
                result.LogPath == Path.Combine(data, UpdateInstaller.LogFileName), "UTF-8 result and trusted local log path");
            Assert(UpdateInstaller.TakeResult(data) == null && File.Exists(Path.Combine(data, "last-update-result.ini")), "result is reported once and archived");
        }
        File.WriteAllText(resultPath, "[Update]\r\nStatus=unexpected\r\n", Encoding.Unicode);
        Assert(UpdateInstaller.TakeResult(data)!.Status == "failed", "unknown result cannot claim success");
        Console.WriteLine("PASS: update installer hash/resources, path/PID validation, shell-free quoted handoff and one-time Unicode result reporting");
    }

    private static string Hash(FileStream file)
    {
        file.Position = 0;
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
    }

    private static string? FindRepository()
    {
        for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "installer", "ProgramManager.iss"))) return directory.FullName;
        return null;
    }

    private static string[] SplitArguments(string arguments)
    {
        var pointer = CommandLineToArgvW("test.exe " + arguments, out var count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("Command-line parsing failed");
        try
        {
            var result = new string[count - 1];
            for (var i = 1; i < count; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(pointer); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("UpdateInstaller: " + message); }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException) { return; }
        throw new InvalidOperationException("UpdateInstaller accepted: " + message);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
