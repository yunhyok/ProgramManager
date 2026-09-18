using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using ProgramManager;
using ProgramManager.Core;

internal static class ZipDownloadChecks
{
    public static void Run(string root)
    {
        DownloadFromPinnedHost(root);
        var folder = Path.Combine(root, "zip downloads");
        Directory.CreateDirectory(folder);
        var first = Path.Combine(root, "verified-first.tmp");
        File.WriteAllText(first, "first");
        var firstDestination = ArchiveDownloads.MoveVerified(first, "Friendly Package.zip", folder);
        var second = Path.Combine(root, "verified-second.tmp");
        File.WriteAllText(second, "second");
        var secondDestination = ArchiveDownloads.MoveVerified(second, "Friendly Package.zip", folder);
        Assert(Path.GetFileName(firstDestination) == "Friendly Package.zip" && File.ReadAllText(firstDestination) == "first", "verified ZIP keeps its original friendly name");
        Assert(Path.GetFileName(secondDestination) == "Friendly Package (1).zip" && File.ReadAllText(firstDestination) == "first" && File.ReadAllText(secondDestination) == "second", "existing Downloads file is preserved with a collision suffix");
        Assert(Path.IsPathRooted(ArchiveDownloads.DownloadsFolder()), "Windows known-folder API resolves an absolute Downloads path");
        Reject(() => ArchiveDownloads.MoveVerified(secondDestination, "not-an-archive.exe", folder), "non-ZIP archive destination");
        Reject(() => ArchiveDownloads.MoveVerified(secondDestination, "..\\escape.zip", folder), "path-like archive destination");

        var release = new AppRelease { Version = "2.0", Platform = Platforms.Current, FileName = "Friendly Package.zip", Size = 10, Sha256 = new string('A', 64), IsPrerelease = true };
        var local = new LocalProgram { Path = Application.ExecutablePath, InstalledVersion = "1.0", InstalledPlatform = Platforms.Current };
        Assert(!InstalledPrograms.IsUpdate(local, release) && !InstalledPrograms.ConfirmsInstall(local, release, 0), "ZIP never enters installer update or auto-registration checks");
        var app = new CatalogApp { Id = "zip-check", Name = "ZIP Check", Releases = [release] };
        var stale = new AppRelease { Version = release.Version, Platform = release.Platform, FileName = release.FileName, Size = release.Size, Sha256 = release.Sha256 };
        Assert(!MainForm.SameInstaller(app, release, app, stale), "fresh metadata verification rejects a trial/stable identity change");

        var state = new AppState(Path.Combine(root, "zip cancellation"));
        state.Settings.ManagerAutoCheck = state.Settings.AppAutoCheck = false;
        var before = System.Text.Json.JsonSerializer.Serialize(state.Settings);
        using var form = new MainForm(state, false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("_remote", flags)!.SetValue(form, new Catalog { Apps = [app] });
        typeof(MainForm).GetMethod("Render", flags)!.Invoke(form, null);
        var catalog = (DataGridView)typeof(MainForm).GetField("_catalog", flags)!.GetValue(form)!;
        var action = Descendants(form).OfType<Button>().Single(button => button.Text == "ZIP 다운로드");
        Assert(action.Enabled && Convert.ToString(catalog.Rows[0].Cells[2].Value)!.Contains("시험판") && Convert.ToString(catalog.Rows[0].Cells[3].Value) == "ZIP 다운로드 가능", "catalog identifies a trial ZIP as a download instead of an install/update");
        using var cancel = new System.Windows.Forms.Timer { Interval = 10 };
        cancel.Tick += (_, _) =>
        {
            var dialog = FindWindow("#32770", "ZIP 다운로드 · " + Program.DisplayName);
            if (dialog != IntPtr.Zero) PostMessage(dialog, 0x0111, (IntPtr)2, IntPtr.Zero);
        };
        cancel.Start();
        ((Task)typeof(MainForm).GetMethod("InstallAsync", flags)!.Invoke(form, null)!).GetAwaiter().GetResult();
        cancel.Stop();
        Assert(System.Text.Json.JsonSerializer.Serialize(state.Settings) == before, "cancelled ZIP warning leaves settings and registrations unchanged");
        Assert(!(bool)typeof(MainForm).GetField("_busy", flags)!.GetValue(form)!, "cancelled ZIP warning starts no download or install operation");
        Console.WriteLine("PASS: pinned-host ZIP download, warning cancellation, no installer registration, native Downloads path, friendly naming and collision preservation");
    }

    private static void DownloadFromPinnedHost(string root)
    {
        var fixture = Path.Combine(root, "accepted ZIP download");
        var source = Path.Combine(fixture, "Accepted Archive.zip");
        Directory.CreateDirectory(fixture);
        var content = System.Text.Encoding.UTF8.GetBytes("verified ZIP fixture");
        File.WriteAllBytes(source, content);
        var storeRoot = Path.Combine(fixture, "catalog");
        var store = new CatalogStore(storeRoot);
        var package = Path.Combine(storeRoot, "packages", "accepted-zip", "1.0", Platforms.Current, "installer.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        File.Copy(source, package);
        JsonFiles.Write(Path.Combine(storeRoot, "catalog.json"), new Catalog { Apps = [new CatalogApp { Id = "accepted-zip", Name = "Accepted ZIP", Releases = [new AppRelease { Version = "1.0", Platform = Platforms.Current, FileName = Path.GetFileName(source), Size = content.Length, Sha256 = Compat.Hash(content), PublishedUtc = DateTimeOffset.UtcNow }] }] });
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(fixture, "identity"));
        var pairing = identity.CreatePairing("127.0.0.1", FreePort());
        using var server = new CatalogServer(store, identity);
        Task.Run(() => server.StartAsync(pairing.Port)).GetAwaiter().GetResult();
        try
        {
            var state = new AppState(Path.Combine(fixture, "client"));
            var settings = AppState.Clone(state.Settings);
            settings.ManagerAutoCheck = settings.AppAutoCheck = false;
            settings.PairingProtected = Secrets.Protect(pairing.Export());
            state.CommitSettings(settings);
            var app = store.Read().Apps.Single();
            using var form = new MainForm(state, false);
            var method = typeof(MainForm).GetMethod("DownloadArchiveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var destination = Path.Combine(fixture, "downloads");
            var task = (Task)method.Invoke(form, new object?[] { app, app.Releases.Single(), destination })!;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
            Assert(task.IsCompleted, "accepted ZIP download finishes within deadline");
            task.GetAwaiter().GetResult();
            var downloaded = Directory.GetFiles(destination, "*.zip", SearchOption.TopDirectoryOnly).Single();
            Assert(Path.GetFileName(downloaded) == "Accepted Archive.zip" && File.ReadAllBytes(downloaded).SequenceEqual(content), "accepted ZIP is freshly verified and saved under its original name");
            Assert(!Directory.EnumerateDirectories(destination).Any(), "same-volume staging directory is removed after download");
            Assert(state.Settings.Programs.Count == 0, "successful ZIP download creates no program registration");
        }
        finally { Task.Run(server.StopAsync).GetAwaiter().GetResult(); }
    }

    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); }
    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("FAIL: should reject " + name);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint!).Port;
        listener.Stop();
        return port;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
