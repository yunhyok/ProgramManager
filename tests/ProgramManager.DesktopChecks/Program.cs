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
#if !NET48
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--render") { Render(root, args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--layout-check")
            {
                var failures = new List<string>();
                var observations = new List<string>();
                foreach (var percent in new[] { 0, 100, 125, 150, 200, 250 })
                {
                    var run = new LayoutCheck(percent, failures);
                    Render(Path.Combine(root, percent.ToString()), Path.Combine(args[1], percent == 0 ? "native" : "simulated-" + percent), run);
                    observations.AddRange(run.Observations);
                }
                Directory.CreateDirectory(args[1]);
                File.WriteAllLines(Path.Combine(args[1], "layout-report.txt"), new[] { "Native run uses production SystemAware DPI. Other runs simulate scaled fonts/geometry in a constrained logical viewport; they do not change monitor DPI or Windows settings." }.Concat(observations).Concat(failures.Count == 0 ? new[] { "PASS: all layout checks" } : failures));
                Assert(failures.Count == 0, failures.Count + " layout issues; see layout-report.txt");
                return 0;
            }
            Checks(root);
            CheckRecentPrograms(root);
            Console.WriteLine("PASS: copied Windows shortcut preserves target/arguments/working directory; deletion-safe launcher; stable dedup; settings/program rollback; corrupt/null JSON rejection; platform numeric ordering; recent five history and tray dispatch");
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

    private static void Render(string root, string outputDirectory, LayoutCheck? layout = null)
    {
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
        layout?.Prepare(form);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("_remote", flags)!.SetValue(form, sampleCatalog);
        typeof(MainForm).GetField("_fingerprint", flags)!.SetValue(form, new string('A', 64));
        typeof(MainForm).GetMethod("Render", flags)!.Invoke(form, null);
        layout?.CheckActions(form);
        var tabs = (TabControl)typeof(MainForm).GetField("_tabs", flags)!.GetValue(form)!;
        for (var index = 0; index < tabs.TabCount; index++)
        {
            tabs.SelectedIndex = index;
            form.PerformLayout();
            Application.DoEvents();
            layout?.Inspect(form, new[] { "local", "catalog", "host" }[index]);
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            var path = Path.GetFullPath(Path.Combine(outputDirectory, "program-manager-" + new[] { "local", "catalog", "host" }[index] + ".png"));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine(path);
        }
        foreach (var dialogName in new[] { "register", "github-settings", "repositories", "settings", "pairing", "history", "help" })
        {
            EventHandler? capture = null;
            capture = (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(f => f != form && f.Modal);
                if (dialog is null) return;
                if (dialogName == "help")
                {
                    var browser = dialog.Controls.OfType<WebBrowser>().Single();
                    if (browser.ReadyState != WebBrowserReadyState.Complete) return;
                    Assert(browser.Document?.GetElementById("host") != null && browser.Document?.Body?.InnerText?.Contains("GitHub") == true && browser.Url?.Fragment == "#host", "installed HTML viewer loads real guide and selected section without browser association");
                    browser.Navigate("https://example.invalid/");
                    Assert(browser.Url?.IsFile == true, "offline viewer blocks external navigation");
                }
                Application.Idle -= capture;
                layout?.Prepare(dialog);
                layout?.Inspect(dialog, dialogName);
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
                else if (dialogName == "github-settings") GitHubDialogs.Settings(form, state.Settings);
                else if (dialogName == "repositories") GitHubDialogs.Sources(form, "sample-account", new List<GitHubRepository> { new() { FullName = "sample-account/IntraDrop", Description = "내부망 파일 전송", Private = false }, new() { FullName = "sample-account/spd-decap-pi-evaluator", Description = "SPD 파일 분석 및 전원 무결성 평가", Private = true } }, new List<GitHubSelection> { new() { Repository = "sample-account/IntraDrop" } });
                else if (dialogName == "settings") Dialogs.Settings(form, state);
                else if (dialogName == "pairing") Dialogs.ShowPairing(form, "Test-only connection code. No real host credentials.", "192.0.2.10:45672");
                else if (dialogName == "help") typeof(MainForm).GetMethod("ShowHelp", flags)!.Invoke(form, null);
                else typeof(MainForm).GetMethod("ShowHostHistory", flags)!.Invoke(form, null);
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
        var githubSettings = AppState.Clone(load.Settings);
        githubSettings.GitHubOwner = "sample-account";
        githubSettings.UseGitHubCli = false;
        githubSettings.CacheRetentionDays = 14;
        githubSettings.GitHubRepositories.Add(new GitHubSelection { Repository = "sample-account/private-tool" });
        var programsBefore = JsonSerializer.Serialize(load.Settings.Programs);
        load.CommitSettings(githubSettings);
        var reloaded = new AppState(appRoot).Settings;
        Assert(JsonSerializer.Serialize(reloaded.Programs) == programsBefore && reloaded.GitHubRepositories.Count == 1 && reloaded.CacheRetentionDays == 14, "GitHub settings preserve local registrations");
        CheckGitHubSelection();
        var consentApp = new CatalogApp { Id = "sample", GitHubRepository = "sample-account/tool" };
        var consentRelease = new AppRelease { Version = "1.2.3", Platform = "win10-x64", FileName = "Setup.exe", Size = 123, GitHubAssetId = 1, GitHubTag = "v1.2.3", PublishedUtc = DateTimeOffset.UtcNow };
        Assert(MainForm.SameInstaller(consentApp, consentRelease, consentApp, consentRelease), "unchanged digestless installer consent");
        foreach (var mutate in new Action<AppRelease>[] { r => r.GitHubAssetId++, r => r.GitHubTag = "1.2.3", r => r.FileName = "Other.exe", r => r.Size++, r => r.Sha256 = new string('A', 64), r => r.PublishedUtc = r.PublishedUtc.AddSeconds(1) })
        {
            var changed = JsonSerializer.Deserialize<AppRelease>(JsonSerializer.Serialize(consentRelease))!;
            mutate(changed);
            Assert(!MainForm.SameInstaller(consentApp, consentRelease, consentApp, changed), "changed installer identity invalidates consent");
        }
        Assert(!MainForm.SameInstaller(consentApp, consentRelease, new CatalogApp { Id = consentApp.Id, GitHubRepository = "sample-account/other" }, consentRelease), "changed repository invalidates consent");
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

    private static void CheckGitHubSelection()
    {
        using var owner = new Form();
        EventHandler? save = null;
        save = (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Modal);
            if (dialog is null) return;
            Application.Idle -= save;
            var layout = (TableLayoutPanel)dialog.Controls[0];
            var grid = layout.Controls.OfType<DataGridView>().Single();
            Assert(grid.Rows.Count == 2 && Convert.ToBoolean(grid.Rows.Cast<DataGridViewRow>().Single(r => (string)r.Cells[1].Value == "sample-account/private-tool").Cells[0].Value), "inaccessible prior repository remains checked");
            ((Button)dialog.AcceptButton!).PerformClick();
        };
        Application.Idle += save;
        try
        {
            var selected = GitHubDialogs.Sources(owner, "sample-account", [new GitHubRepository { FullName = "sample-account/public-tool" }], [new GitHubSelection { Repository = "sample-account/private-tool", LegacyAssetPattern = "*win7*.exe" }]);
            Assert(selected?.Count == 1 && selected[0].Repository == "sample-account/private-tool" && selected[0].LegacyAssetPattern == "*win7*.exe", "selection save preserves invisible repository and platform pattern");
        }
        finally { Application.Idle -= save; }
    }

    private static void CheckRecentPrograms(string root)
    {
        var appRoot = Path.Combine(root, "recent-programs");
        var state = new AppState(appRoot);
        var programs = new List<LocalProgram>();
        for (var index = 0; index < 7; index++)
        {
            var path = Path.Combine(appRoot, "recent-" + index + ".exe");
            File.WriteAllBytes(path, new byte[] { 0x4d, 0x5a }); // Registered test files are never executed.
            programs.Add(state.SaveProgram(new LocalProgram { Name = "Recent program " + index, Path = path }));
        }
        var settingsPath = Path.Combine(appRoot, "settings.json");
        // Exercise the on-disk settings format used before recent history existed.
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new { Programs = state.Settings.Programs, AutoStart = state.Settings.AutoStart }));
        state = new AppState(appRoot);
        Assert(state.Settings.RecentProgramIds.Count == 0 && state.RecentPrograms.Count == 0 && state.Settings.Programs.Count == 7, "legacy settings keep programs and default to empty recent history");
        using var form = new MainForm(state, false);
        using var menu = new ContextMenuStrip();
        form.PopulateTrayMenu(menu);
        Assert(menu.Items[0].Text == "최근 실행" && !menu.Items[0].Enabled && menu.Items[1].Tag is null && !menu.Items[1].Enabled, "empty recent tray has disabled heading and placeholder");

        foreach (var program in programs) state.RecordLaunch(program.Id);
        var latestFive = programs.Skip(2).Reverse().Select(p => p.Id).ToArray();
        Assert(state.Settings.RecentProgramIds.SequenceEqual(latestFive) && state.RecentPrograms.Select(p => p.Id).SequenceEqual(latestFive), "seven launches retain five most recent IDs in descending order");
        state.RecordLaunch(programs[3].Id);
        var deduplicated = new[] { programs[3].Id, programs[6].Id, programs[5].Id, programs[4].Id, programs[2].Id };
        Assert(state.Settings.RecentProgramIds.SequenceEqual(deduplicated), "repeat launch moves existing entry to top without duplicates");
        Assert(new AppState(appRoot).RecentPrograms.Select(p => p.Id).SequenceEqual(deduplicated), "recent history survives settings reload");

        var beforeFailedSave = File.ReadAllText(settingsPath);
        using (var locked = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Reject(() => state.RecordLaunch(programs[0].Id), "recent history settings write failure");
            Assert(state.Settings.RecentProgramIds.SequenceEqual(deduplicated), "failed history write leaves live order unchanged");
        }
        Assert(File.ReadAllText(settingsPath) == beforeFailedSave, "failed history write preserves settings file");

        var renamed = AppState.Clone(state.Settings.Programs.Single(p => p.Id == programs[3].Id));
        renamed.Name = "Renamed & current";
        state.SaveProgram(renamed);
        Assert(state.RecentPrograms[0].Name == renamed.Name && ReferenceEquals(state.RecentPrograms[0], state.Settings.Programs.Single(p => p.Id == renamed.Id)), "recent entries resolve current registered objects and renamed names");
        form.PopulateTrayMenu(menu);
        Assert(RecentItems(menu).First().Text == "Renamed && current", "recent menu escapes ampersands without changing program names");
        var next = AppState.Clone(state.Settings);
        next.Programs.RemoveAll(p => p.Id == programs[3].Id);
        state.CommitSettings(next);
        Assert(state.RecentPrograms.Select(p => p.Id).SequenceEqual(deduplicated.Skip(1)), "deleted registration disappears from recent programs");
        Assert(new AppState(appRoot).RecentPrograms.All(p => p.Id != programs[3].Id), "deleted registration stays absent after reload");

        var missing = state.Settings.Programs.Single(p => p.Id == programs[6].Id);
        File.Delete(missing.Path);
        var beforeFailedLaunch = state.Settings.RecentProgramIds.ToArray();
        Reject(() => state.Launch(missing), "missing executable launch");
        Assert(state.Settings.RecentProgramIds.SequenceEqual(beforeFailedLaunch), "failed launch does not change recent history");
        form.PopulateTrayMenu(menu);
        Assert(!RecentItems(menu).Single(item => (string)item.Tag! == missing.Id).Enabled, "recent menu disables missing executable");

        var search = (TextBox)typeof(MainForm).GetField("_search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        var localGrid = (DataGridView)typeof(MainForm).GetField("_local", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        search.Text = "no-matching-program-" + Guid.NewGuid().ToString("N");
        Assert(localGrid.Rows.Count == 0, "search removes all visible local rows for tray independence check");
        form.PopulateTrayMenu(menu);
        Assert(RecentItems(menu).Select(item => (string)item.Tag!).SequenceEqual(state.RecentPrograms.Select(p => p.Id)), "recent tray is independent of visible list filter");
        var busy = typeof(MainForm).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!;
        busy.SetValue(form, true);
        try
        {
            form.PopulateTrayMenu(menu);
            Assert(RecentItems(menu).All(item => !item.Enabled), "recent launch menu disabled during a managed operation");
        }
        finally { busy.SetValue(form, false); }

        // No user application is launched: rundll32 without a DLL/entrypoint exits immediately.
        var harmlessPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rundll32.exe");
        Assert(File.Exists(harmlessPath), "Windows no-argument launch fixture exists");
        var harmless = state.SaveProgram(new LocalProgram { Name = "Windows no-argument launch", Path = harmlessPath });
        state.RecordLaunch(harmless.Id);
        state.RecordLaunch(programs[0].Id);
        form.PopulateTrayMenu(menu);
        var clickable = RecentItems(menu).Single(item => (string)item.Tag! == harmless.Id);
        Assert(clickable.Enabled && state.Settings.RecentProgramIds[0] != harmless.Id, "registered harmless launch is available below most recent entry");
        // A stale caller must resolve the registered path instead of launching caller-provided data.
        harmless.Name = "Current & launch";
        state.SaveProgram(harmless);
        var stale = AppState.Clone(harmless);
        stale.Path = Path.Combine(appRoot, "stale-path-must-not-run.exe");
        state.Launch(stale);
        state.RecordLaunch(programs[0].Id);
        clickable.PerformClick();
        Assert(state.Settings.RecentProgramIds[0] == harmless.Id && state.RecentPrograms[0].Name == "Current & launch", "actual tray click launches current registered program and records it first");
        Assert(new AppState(appRoot).RecentPrograms[0].Id == harmless.Id, "tray click history is persisted");
        form.PopulateTrayMenu(menu);
        Assert(RecentItems(menu).First().Text == "Current && launch" && RecentItems(menu).Count() <= 5, "reopened tray shows current escaped name and at most five entries");

        var artifactDirectory = Environment.GetEnvironmentVariable("PROGRAM_MANAGER_TRAY_CHECK_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            artifactDirectory = Path.GetFullPath(artifactDirectory);
            Directory.CreateDirectory(artifactDirectory);
            menu.Show(new Point(40, 40));
            Application.DoEvents();
            using var bitmap = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, menu.Size));
            bitmap.Save(Path.Combine(artifactDirectory, "program-manager-recent-tray.png"), System.Drawing.Imaging.ImageFormat.Png);
            File.WriteAllLines(Path.Combine(artifactDirectory, "program-manager-recent-tray.txt"), menu.Items.Cast<ToolStripItem>().Select(item => $"{item.GetType().Name}: text={item.Text}; enabled={item.Enabled}; bounds={item.Bounds}; registeredId={item.Tag}"));
            menu.Close();
        }
    }

    private static IEnumerable<ToolStripMenuItem> RecentItems(ContextMenuStrip menu) => menu.Items.OfType<ToolStripMenuItem>().Where(item => item.Tag is string id && Guid.TryParse(id, out _));

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
