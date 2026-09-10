using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ProgramManager;
using ProgramManager.Core;

internal static class DesktopCheckRunner
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProgramManagerDesktopChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (args.Length == 2 && args[0] == "--render") { Render(root, args[1]); return 0; }
            Checks(root);
            Console.WriteLine("PASS: copied Windows shortcut preserves target/arguments/working directory; deletion-safe launcher; stable dedup; settings/program rollback; corrupt/null JSON rejection; platform numeric ordering");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("ProgramManagerDesktopChecks-")) throw new InvalidOperationException("Unexpected test path");
            Directory.Delete(full, true);
        }
    }

    private static void Render(string root, string outputDirectory)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(outputDirectory);
        var state = new AppState(Path.Combine(root, "state"));
        var localSettings = Clone(state.Settings);
        var sampleCatalog = new Catalog();
        var samples = new[] { ("intra-drop", "Intra Drop", "1.4.0", "1.4.1", "내부망 파일 전송 및 PC 간 공유"), ("spd-cap-injector", "SPD Cap Injector", "0.1.6", "0.1.6", "SPD 부품 번호 정리 및 커패시터 정보 편집"), ("pi-calculator", "PI Calculator", "0.22.7", "0.23.0", "전원 무결성 분석 및 디커플링 설계 검토") };
        foreach (var sample in samples)
        {
            var file = Path.Combine(root, sample.Item1 + ".exe");
            File.WriteAllBytes(file, new byte[] { 0x4d, 0x5a });
            localSettings.Programs.Add(new LocalProgram { Name = sample.Item2, Path = file, InstalledVersion = sample.Item3, CatalogId = sample.Item1, HostFingerprint = new string('A', 64), InstalledPlatform = Platforms.Current });
            sampleCatalog.Apps.Add(new CatalogApp { Id = sample.Item1, Name = sample.Item2, Description = sample.Item5, Releases = [new AppRelease { Version = sample.Item4, Platform = Platforms.Modern, Notes = "실행 안정성 개선 및 사용자 편의 기능 업데이트\r\n변경 내역을 확인한 뒤 설치할 수 있습니다.", FileName = "setup.exe", Size = 64000000, Sha256 = new string('B', 64), PublishedUtc = DateTimeOffset.UtcNow }, new AppRelease { Version = sample.Item3, Platform = Platforms.Legacy, Notes = "Windows 7용 호환 설치 파일", FileName = "setup-win7.exe", Size = 41000000, Sha256 = new string('C', 64), PublishedUtc = DateTimeOffset.UtcNow.AddDays(-4) }] });
        }
        // Test data is injected after startup; no real pairing code or external network connection is used.
        state.CommitSettings(localSettings);
        state.Cache = new CachedCatalog { Fingerprint = new string('A', 64), CheckedUtc = DateTimeOffset.Now, Catalog = sampleCatalog };
        state.SaveCache();
        JsonFiles.Write(Path.Combine(state.Root, "repository", "catalog.json"), sampleCatalog);
        using var form = new MainForm(state, false);
        form.Show();
        Application.DoEvents();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("_remote", flags)!.SetValue(form, sampleCatalog);
        typeof(MainForm).GetField("_fingerprint", flags)!.SetValue(form, new string('A', 64));
        typeof(MainForm).GetMethod("Render", flags)!.Invoke(form, null);
        var tabs = (TabControl)typeof(MainForm).GetField("_tabs", flags)!.GetValue(form)!;
        for (var index = 0; index < tabs.TabCount; index++)
        {
            tabs.SelectedIndex = index;
            form.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            var path = Path.GetFullPath(Path.Combine(outputDirectory, "program-manager-" + new[] { "local", "catalog", "host" }[index] + ".png"));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(path);
        }
        foreach (var dialogName in new[] { "register", "publish", "settings" })
        {
            EventHandler? capture = null;
            capture = (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(f => f != form && f.Modal);
                if (dialog is null) return;
                Application.Idle -= capture;
                using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                var path = Path.GetFullPath(Path.Combine(outputDirectory, "program-manager-" + dialogName + ".png"));
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine(path);
                dialog.DialogResult = DialogResult.Cancel;
            };
            Application.Idle += capture;
            try
            {
                if (dialogName == "register") Dialogs.EditLocal(form, state.Settings.Programs[0]);
                else if (dialogName == "publish") Dialogs.Publish(form, sampleCatalog.Apps[0]);
                else Dialogs.Settings(form, state);
            }
            finally { Application.Idle -= capture; }
        }
        form.Close();
        Assert(!form.Visible && !form.IsDisposed, "window close keeps tray application alive");
    }

    private static void Checks(string root)
    {
        var appRoot = Path.Combine(root, "state");
        var state = new AppState(appRoot);
        var source = Path.Combine(root, "desktop-example.lnk");
        var executable = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var arguments = "/c echo Program Manager shortcut check";
        MakeShortcut(source, executable, arguments, root);
        var originalBytes = File.ReadAllBytes(source);
        var item = state.SaveProgram(new LocalProgram { Name = "Example", Path = source });
        Assert(item.Path != source && File.ReadAllBytes(item.Path).SequenceEqual(originalBytes), "managed byte-for-byte shortcut copy");
        Assert(item.SourcePath == source, "original shortcut provenance");
        var managed = item.Path;
        ReadShortcut(managed, executable, arguments, root);
        var again = state.SaveProgram(new LocalProgram { Name = "Renamed", Path = source });
        Assert(again.Id == item.Id && state.Settings.Programs.Count == 1, "deduplicated original shortcut with stable ID");
        File.Delete(source);
        Assert(File.Exists(again.Path), "desktop shortcut deletion leaves launcher working");
        ReadShortcut(again.Path, executable, arguments, root);
        var exeItem = state.SaveProgram(new LocalProgram { Name = "Command", Path = executable });
        var exeAgain = state.SaveProgram(new LocalProgram { Name = "Command renamed", Path = executable });
        Assert(exeAgain.Id == exeItem.Id && state.Settings.Programs.Count == 2, "executable path dedup");
        var settingsPath = Path.Combine(appRoot, "settings.json");
        var previousJson = File.ReadAllText(settingsPath);
        var previousSettings = state.Settings;
        var next = Clone(state.Settings);
        next.HostEnabled = !next.HostEnabled;
        next.AdvertisedHost = "updated-host";
        next.Port = 45673;
        var anotherShortcut = Path.Combine(root, "another.lnk");
        MakeShortcut(anotherShortcut, executable, "/c echo another", root);
        var ownedBefore = Directory.GetFiles(Path.Combine(appRoot, "shortcuts"));
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Reject(() => state.CommitSettings(next), "settings save failure");
            Assert(ReferenceEquals(state.Settings, previousSettings) && state.Settings.AdvertisedHost != "updated-host", "live settings unchanged after failed save");
            Reject(() => state.SaveProgram(new LocalProgram { Name = "Failure", Path = anotherShortcut }), "shortcut registration save failure");
            Assert(ReferenceEquals(state.Settings, previousSettings) && state.Settings.Programs.Count == 2, "program list unchanged after failed save");
            Assert(Directory.GetFiles(Path.Combine(appRoot, "shortcuts")).OrderBy(p => p).SequenceEqual(ownedBefore.OrderBy(p => p)), "new shortcut removed after failed save");
        }
        Assert(previousJson == File.ReadAllText(settingsPath), "previous settings file intact");
        Assert(File.Exists(anotherShortcut), "user shortcut never removed");
        state.CommitSettings(next);
        next.Programs.Clear(); next.AdvertisedHost = "mutated-after-commit";
        Assert(state.Settings.AdvertisedHost == "updated-host" && state.Settings.Programs.Count == 2, "committed settings isolated from caller");
        var invalid = Clone(state.Settings); invalid.Programs[0].InstalledVersion = null!;
        Reject(() => state.CommitSettings(invalid), "null local version");
        invalid = Clone(state.Settings); invalid.PairingProtected = null!;
        Reject(() => state.CommitSettings(invalid), "null pairing");
        invalid = Clone(state.Settings); invalid.Programs = null!;
        Reject(() => state.CommitSettings(invalid), "null program list");
        invalid = Clone(state.Settings); invalid.Port = 0;
        Reject(() => state.CommitSettings(invalid), "invalid port");
        Reject(() => state.SaveProgram(new LocalProgram { Id = "../../escape", Name = "Invalid", Path = executable }), "hostile shortcut identity");
        var load = new AppState(appRoot);
        Assert(load.Settings.Programs.Count == 2 && File.Exists(load.Settings.Programs[0].Path), "persisted programs reload");
        var cachePath = Path.Combine(appRoot, "cache.json");
        File.WriteAllText(cachePath, "{\"Catalog\":null}");
        Reject(() => new AppState(appRoot), "null cached catalog");
        Assert(File.ReadAllText(cachePath) == "{\"Catalog\":null}", "corrupt cache preserved");
        File.WriteAllText(cachePath, "{\"Catalog\":{\"Apps\":null}}");
        Reject(() => new AppState(appRoot), "null cached app list");
        File.Delete(cachePath);
        File.WriteAllText(settingsPath, "{\"Programs\":[{\"Id\":\"" + Guid.NewGuid().ToString("N") + "\",\"Name\":\"Test\",\"Path\":null}]}");
        Reject(() => new AppState(appRoot), "null persisted local path");
        var versions = new CatalogApp { Releases = [new AppRelease { Version = "1.9", Platform = "win7" }, new AppRelease { Version = "1.10.0", Platform = "win7" }, new AppRelease { Version = "2.0", Platform = "win10-x64" }] };
        Assert(Platforms.Latest(versions, Platforms.Legacy)!.Version == "1.10.0" && Platforms.Latest(versions, Platforms.Modern)!.Version == "2.0", "target-specific numeric latest");
    }

    private static void MakeShortcut(string path, string target, string arguments, string workingDirectory)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            foreach (var pair in new[] { ("TargetPath", target), ("Arguments", arguments), ("WorkingDirectory", workingDirectory) })
                shortcut!.GetType().InvokeMember(pair.Item1, BindingFlags.SetProperty, null, shortcut, [pair.Item2]);
            shortcut!.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }

    private static void ReadShortcut(string path, string target, string arguments, string workingDirectory)
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            foreach (var pair in new[] { ("TargetPath", target), ("Arguments", arguments), ("WorkingDirectory", workingDirectory) })
                Assert(string.Equals((string?)shortcut!.GetType().InvokeMember(pair.Item1, BindingFlags.GetProperty, null, shortcut, null), pair.Item2, StringComparison.OrdinalIgnoreCase), "shortcut " + pair.Item1);
        }
        finally { if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }

    private static UserSettings Clone(UserSettings settings) => JsonSerializer.Deserialize<UserSettings>(JsonSerializer.SerializeToUtf8Bytes(settings))!;
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { return; }
        throw new Exception("FAIL: should reject " + name);
    }
}
