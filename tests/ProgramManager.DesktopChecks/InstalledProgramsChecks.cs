using ProgramManager;
using ProgramManager.Core;
using System.Reflection;

internal static class InstalledProgramsChecks
{
    public static void Run(string root)
    {
        var folder = Path.Combine(root, "installed-programs"); Directory.CreateDirectory(folder);
        // Read existing binaries and use in-memory uninstall records; never imitate an
        // installation by copying executables or writing registry entries on the user's PC.
        var executable = Application.ExecutablePath;
        var name = "Detection Example " + Guid.NewGuid().ToString("N");
        var app = new CatalogApp { Id = "detection-example", Name = name, Releases = [new AppRelease { Version = "2.0", Platform = Platforms.Current }] };
        {
            var actual = InstalledPrograms.Read();
            Assert(actual.Entries.All(e => e.Key.Length > 0 && e.Name.Length > 0), "native installed inventory can be read without registry mutation");
            var inventory = new InstalledPrograms();
            inventory.Entries.Add(new InstalledPrograms.Entry { Key = "in-memory-installation", Name = name, Version = "1.2.3", Icon = "\"" + executable + "\",0", Location = Path.GetDirectoryName(executable)! });
            var detected = inventory.Find(app, null, out var reason);
            Assert(detected != null && detected.Path == executable && detected.InstalledVersion == "1.2.3" && reason.Length == 0, "uninstall entry and quoted icon find actual path/version");
            Assert(detected!.InstalledVersion != app.Releases[0].Version, "catalog version is never copied into detected installation");
            Assert(InstalledPrograms.NumericVersion("v1.2.3+commit") == "1.2.3" && InstalledPrograms.NumericVersion("unknown") == "" && InstalledPrograms.NumericVersion("0.0.0.0") == "", "version metadata parses numeric prefix and rejects unknown/zero");
            Assert(InstalledPrograms.FileVersion(executable).Length > 0, "real executable version resources can be read");
            Assert(!InstalledPrograms.ConfirmsInstall(detected, app.Releases[0], 0), "zero exit alone cannot confirm an old installation");
            detected.InstalledVersion = "2.0";
            Assert(InstalledPrograms.ConfirmsInstall(detected, app.Releases[0], 0) && InstalledPrograms.ConfirmsInstall(detected, app.Releases[0], 3010), "actual version confirms successful or reboot-required setup");
            Assert(!InstalledPrograms.ConfirmsInstall(detected, app.Releases[0], 1602), "canceled setup never confirms installation");
            detected.InstalledVersion = "";
            Assert(!InstalledPrograms.ConfirmsInstall(detected, app.Releases[0], 0), "unknown version does not invent an installation result");

            var state = new AppState(Path.Combine(folder, "manager"));
            var settings = AppState.Clone(state.Settings); settings.ManagerAutoCheck = settings.AppAutoCheck = false; state.CommitSettings(settings);
            var shortcut = Path.Combine(folder, "Custom shortcut.lnk");
            DesktopCheckRunner.MakeShortcut(shortcut, executable, "--profile custom", folder);
            var previous = state.SaveProgram(new LocalProgram { Name = "My custom name", Path = shortcut, InstalledVersion = "1.0" });
            var previousBytes = File.ReadAllBytes(previous.Path);
            using var form = new MainForm(state, false);
            form.Show(); Application.DoEvents(); PumpUntil(() => !(bool)typeof(MainForm).GetField("_busy", Flags)!.GetValue(form)!);
            typeof(MainForm).GetField("_remote", Flags)!.SetValue(form, new Catalog { Apps = [app] });
            typeof(MainForm).GetField("_fingerprint", Flags)!.SetValue(form, new string('A', 64));
            detected = inventory.Find(app, null, out _)!;
            form.SaveDetected(detected, app, new string('A', 64));
            Assert(state.Settings.Programs.Count == 1 && state.Settings.Programs[0].Id == previous.Id && state.Settings.Programs[0].Name == previous.Name, "auto registration reuses an existing launch entry");
            var saved = state.Settings.Programs[0];
            Assert(saved.InstalledVersion == "1.2.3" && saved.CatalogId == app.Id && saved.InstallationKey.Length > 0, "detected version and stable installation identity persist");
            Assert(File.ReadAllBytes(saved.Path).SequenceEqual(previousBytes), "shortcut arguments and bytes are preserved");
            DesktopCheckRunner.ReadShortcut(saved.Path, executable, "--profile custom", folder);
            using var menu = new ContextMenuStrip(); form.PopulateTrayMenu(menu);
            var updates = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "업데이트 1개");
            Assert(updates.DropDownItems[0].Text!.Contains("1.2.3 → 2.0"), "tray identifies current and available versions");
            var tray = (NotifyIcon)typeof(MainForm).GetField("_tray", Flags)!.GetValue(form)!;
            Assert(tray.Text.Contains("업데이트 1개") && tray.Icon == typeof(MainForm).GetField("_appUpdateIcon", Flags)!.GetValue(form), "tray tooltip and badge signal an available update");
            var artifacts = Environment.GetEnvironmentVariable("PROGRAM_MANAGER_TRAY_CHECK_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(artifacts))
            {
                Directory.CreateDirectory(artifacts);
                using var badge = tray.Icon!.ToBitmap(); badge.Save(Path.Combine(artifacts, "program-manager-update-badge.png"));
                menu.Show(new Point(40, 40)); updates.ShowDropDown(); Application.DoEvents();
                using var bitmap = new Bitmap(updates.DropDown.Width, updates.DropDown.Height);
                updates.DropDown.DrawToBitmap(bitmap, new Rectangle(Point.Empty, updates.DropDown.Size));
                bitmap.Save(Path.Combine(artifacts, "program-manager-app-updates.png")); menu.Close();
            }
            ((ToolStripMenuItem)updates.DropDownItems[0]).PerformClick();
            Assert(((TabControl)typeof(MainForm).GetField("_tabs", Flags)!.GetValue(form)!).SelectedIndex == 1, "tray update selection opens the distribution catalog");
            typeof(MainForm).GetField("_managerUpdate", Flags)!.SetValue(form, new ManagerUpdate { Release = new AppRelease { Version = "0.10.0", Platform = Platforms.Current } });
            form.PopulateTrayMenu(menu);
            var combined = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "업데이트 2개");
            Assert(combined.DropDownItems.Count == 2 && combined.DropDownItems[0].Text!.StartsWith("Program Manager ", StringComparison.Ordinal) && combined.DropDownItems[1].Text!.Contains(saved.Name), "self and app updates share one menu with identifiable targets");
            typeof(MainForm).GetField("_managerUpdate", Flags)!.SetValue(form, null);
            var stale = AppState.Clone(saved); stale.InstalledVersion = "0.0.1"; state.SaveProgram(stale);
            var fileVersion = InstalledPrograms.FileVersion(executable);
            app.Releases[0].Version = fileVersion;
            var refresh = (Task)typeof(MainForm).GetMethod("RefreshInstalledAsync", Flags)!.Invoke(form, null)!;
            PumpUntil(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
            Assert(state.Settings.Programs[0].InstalledVersion == fileVersion, "refresh replaces a stale record with actual executable version metadata: actual=" + state.Settings.Programs[0].InstalledVersion + ", expected=" + fileVersion);
            form.PopulateTrayMenu(menu);
            Assert(!menu.Items.Cast<ToolStripItem>().Any(i => i.Text?.Contains("0개") == true) && tray.Icon == form.Icon, "empty update entry and badge disappear after installed version catches up");
            var unknown = AppState.Clone(saved); unknown.InstalledVersion = "";
            Assert(!InstalledPrograms.IsUpdate(unknown, app.Releases[0]), "unknown version never produces a false update");
            var other = AppState.Clone(saved); other.InstalledPlatform = Platforms.Current == Platforms.Modern ? Platforms.Legacy : Platforms.Modern;
            Assert(!InstalledPrograms.IsUpdate(other, app.Releases[0]), "other Windows platform cannot produce an update");

            var ambiguous = new InstalledPrograms();
            ambiguous.Entries.Add(new InstalledPrograms.Entry { Key = "one", Name = name, Version = "1.0", Icon = executable });
            var second = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
            Assert(File.Exists(second), "existing Windows executable used for path checks; never launched");
            ambiguous.Entries.Add(new InstalledPrograms.Entry { Key = "two", Name = name, Version = "1.0", Icon = second });
            Assert(ambiguous.Find(app, null, out reason) is null && reason.Contains("여러"), "ambiguous installations require manual confirmation");
            ambiguous.Entries[1].Icon = executable; ambiguous.Entries[1].Version = "2.0";
            Assert(ambiguous.Find(app, null, out _) is null, "conflicting registry versions for the same executable stay ambiguous");
            var moved = AppState.Clone(state.Settings.Programs[0]);
            var relocated = new InstalledPrograms();
            relocated.Entries.Add(new InstalledPrograms.Entry { Key = moved.InstallationKey, Name = name, Version = "3.0", Icon = second });
            var newPath = relocated.Find(app, moved, out _);
            Assert(newPath?.Path == second && newPath.Id == moved.Id && newPath.InstalledVersion == "3.0", "stable installer identity follows a relocated executable");
            var broad = new InstalledPrograms();
            broad.Entries.Add(new InstalledPrograms.Entry { Key = "other", Name = "Unrelated product", Version = "99.0", Location = Path.GetDirectoryName(executable)! });
            moved.InstallationKey = "";
            Assert(broad.Find(app, moved, out _)?.InstalledVersion != "99.0", "a broad install folder cannot assign another product's version");
            ambiguous.Entries.Clear(); ambiguous.Entries.Add(new InstalledPrograms.Entry { Key = "helper", Name = name, Version = "2.0", Icon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe") });
            Assert(ambiguous.Find(app, null, out _) is null, "uninstall helpers cannot become launch entries");
            var unrelated = inventory.Find(new CatalogApp { Id = "unrelated", Name = "Unrelated program" }, null, out _);
            Assert(unrelated is null, "unrelated installed applications are not registered");
        }
        Console.WriteLine("PASS: read-only installed inventory, file version, cancellation/ambiguity, shortcut-preserving registration, version refresh and tray updates");
    }
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
        Assert(done(), "async UI work finishes");
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException("InstalledPrograms: " + message); }
}
