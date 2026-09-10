using System.Net;
using System.Net.Sockets;
using ProgramManager;
using ProgramManager.Core;

internal static class ConnectionSettingsChecks
{
    public static void Run(string root)
    {
        Task.Run(() => RunAsync(root)).GetAwaiter().GetResult();
        CheckApply(root);
    }

    private static void CheckApply(string root)
    {
        var state = new AppState(Path.Combine(root, "settings-apply"));
        var original = AppState.Clone(state.Settings);
        original.ManagerAutoCheck = false;
        original.GitHubOwner = "preserved-owner";
        original.GitHubTokenProtected = Secrets.Protect("preserved-token");
        original.GitHubRepositories.Add(new GitHubSelection { Repository = "sample/app" });
        state.CommitSettings(original);
        state.SaveProgram(new LocalProgram { Name = "Existing", Path = Environment.GetEnvironmentVariable("COMSPEC")! });
        var programs = System.Text.Json.JsonSerializer.Serialize(state.Settings.Programs);
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(root, "apply-host"));
        using var server = new CatalogServer(new CatalogStore(Path.Combine(root, "connection-settings", "catalog")), identity);
        var pair = identity.CreatePairing("127.0.0.1", FreePort());
        Task.Run(() => server.StartAsync(pair.Port)).GetAwaiter().GetResult();
        using var form = new MainForm(state, false);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var apply = typeof(MainForm).GetMethod("ApplySettingsAsync", flags)!;
        var change = new Dialogs.SettingsChange { Host = state.Settings.AdvertisedHost, Port = state.Settings.Port, AutoStart = state.Settings.AutoStart, NewPairing = pair.Export() };
        void Apply()
        {
            var task = (Task)apply.Invoke(form, new object[] { change, CancellationToken.None })!;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(1); }
            Assert(task.IsCompleted, "settings apply finishes within deadline");
            task.GetAwaiter().GetResult();
        }
        Apply();
        Assert(state.Pairing?.Fingerprint == pair.Fingerprint && state.Cache.Catalog.Apps.Count == 1, "applying a code persists the pairing and received catalog");
        var grid = (DataGridView)typeof(MainForm).GetField("_catalog", flags)!.GetValue(form)!;
        Assert(grid.Rows.Count == 1, "applying a code immediately populates the main catalog");
        Assert(System.Text.Json.JsonSerializer.Serialize(state.Settings.Programs) == programs && state.Settings.GitHubOwner == original.GitHubOwner && state.Settings.GitHubTokenProtected == original.GitHubTokenProtected && state.Settings.GitHubRepositories.Count == 1, "connection changes preserve existing programs and GitHub settings");
        var before = File.ReadAllText(Path.Combine(state.Root, "settings.json"));
        change.NewPairing = "bad-code";
        try { Apply(); throw new Exception("Invalid pairing was saved"); }
        catch (InvalidDataException) { }
        Assert(File.ReadAllText(Path.Combine(state.Root, "settings.json")) == before && grid.Rows.Count == 1, "failed connection leaves saved settings and list intact");
        typeof(MainForm).GetField("_settingsOpen", flags)!.SetValue(form, true);
        var tray = (NotifyIcon)typeof(MainForm).GetField("_tray", flags)!.GetValue(form)!;
        var opening = new System.ComponentModel.CancelEventArgs();
        typeof(ToolStripDropDown).GetMethod("OnOpening", flags)!.Invoke(tray.ContextMenuStrip, new object[] { opening });
        Assert(opening.Cancel, "tray operations cannot overlap the settings dialog");
        Task.Run(server.StopAsync).GetAwaiter().GetResult();
        Console.WriteLine("PASS: settings apply immediately loads catalog, preserves apps/credentials, rejects invalid code without mutation, and excludes concurrent tray actions");
    }

    private static async Task RunAsync(string root)
    {
        foreach (var text in new[] { "", "127.0.0.1", "PM1:not-base64", "PM1:e30=" })
        {
            var invalid = await Reject(() => ConnectionSettings.CheckAsync(text, CancellationToken.None));
            Assert(invalid is InvalidDataException && invalid.Message.Contains("연결 코드"), "missing, manual-IP and malformed codes require the complete pairing code");
        }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var error = await Reject(() => ConnectionSettings.CheckAsync("not-a-code", canceled.Token));
            Assert(error is OperationCanceledException canceledError && canceledError.CancellationToken == canceled.Token, "caller cancellation is honored before parsing");
        }
        var fixture = Path.Combine(root, "connection-settings");
        var store = new CatalogStore(Path.Combine(fixture, "catalog"));
        var installer = Path.Combine(fixture, "Connection-Setup.exe");
        File.WriteAllBytes(installer, new byte[] { 0x4d, 0x5a, 1, 2 });
        await store.PublishAsync("connection-test", "Connection test", "Local TLS fixture", "1.0", "", installer);
        using var identity = HostIdentity.LoadOrCreate(Path.Combine(fixture, "identity"));
        var pairing = identity.CreatePairing("127.0.0.1", FreePort());
        using var server = new CatalogServer(store, identity);
        await server.StartAsync(pairing.Port);
        var catalog = await ConnectionSettings.CheckAsync(pairing.Export(), CancellationToken.None);
        Assert(catalog.Apps.Count == 1 && catalog.Apps[0].Id == "connection-test", "connection check fetches a real authenticated catalog");
        var wrongToken = PairingInfo.Parse(pairing.Export()); wrongToken.Token = new string('0', 64);
        var authentication = await Reject(() => ConnectionSettings.CheckAsync(wrongToken.Export(), CancellationToken.None));
        Assert(authentication is InvalidOperationException && authentication.Message == "호스트 인증에 실패했습니다. 연결 코드를 다시 확인하세요.", "core token-authentication explanation retained");
        var wrongCertificate = PairingInfo.Parse(pairing.Export()); wrongCertificate.Fingerprint = new string('0', 64);
        var certificate = await Reject(() => ConnectionSettings.CheckAsync(wrongCertificate.Export(), CancellationToken.None));
        Assert(certificate.Message.Contains("인증서"), "certificate mismatch has a localized explanation");
        using var unopenedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        unopenedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var unavailable = identity.CreatePairing("127.0.0.1", ((IPEndPoint)unopenedPort.LocalEndPoint!).Port);
        var connection = await Reject(() => ConnectionSettings.CheckAsync(unavailable.Export(), CancellationToken.None));
        Assert(connection.Message.Contains("호스트") && (connection.Message.Contains("포트") || connection.Message.Contains("시간")), "unreachable port has actionable connection guidance");
        await server.StopAsync();
        var addresses = ConnectionSettings.LocalAddresses();
        Assert(addresses.Distinct(StringComparer.Ordinal).Count() == addresses.Length, "local addresses contain no duplicates");
        foreach (var text in addresses)
        {
            Assert(IPAddress.TryParse(text, out var address) && address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address), "local address is a non-loopback IPv4 address");
            var bytes = address!.GetAddressBytes();
            Assert(!(bytes[0] == 169 && bytes[1] == 254) && bytes[0] != 0 && bytes[0] < 224, "local address excludes link-local and non-unicast ranges");
        }
        Console.WriteLine("PASS: connection-code validation, real pinned TLS catalog, auth/connection errors, cancellation and local IPv4 discovery");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException("Connection settings check failed: " + message); }
    private static async Task<Exception> Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException) { return error; }
        throw new InvalidOperationException("Expected connection check failure");
    }
}
