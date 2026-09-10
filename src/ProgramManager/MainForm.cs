using System.Diagnostics;
using ProgramManager.Core;

namespace ProgramManager;

internal sealed class MainForm : Form
{
    private readonly AppState _state;
    private readonly NotifyIcon _tray;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(20, 10) };
    private readonly DataGridView _local = Ui.Grid(("프로그램", 26), ("설치 버전", 13), ("업데이트", 16), ("실행 경로", 45));
    private readonly DataGridView _catalog = Ui.Grid(("프로그램", 29), ("설치 버전", 15), ("배포 버전", 15), ("상태", 16), ("설명", 35));
    private readonly DataGridView _host = Ui.Grid(("프로그램", 26), ("ID", 23), ("Win 10/11", 15), ("Win 7/8", 15), ("배포본 수", 12));
    private readonly TextBox _search = new() { Width = 250, AccessibleName = "프로그램 검색" };
    private readonly ComboBox _platform = new() { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "대상 Windows" };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.None, AccessibleName = "프로그램 설명과 버전 기록" };
    private readonly Label _summary = Ui.Label("", 10);
    private readonly Label _connection = Ui.Label("", 9);
    private readonly Label _hostStatus = Ui.Label("호스트 기능을 켜면 다른 PC에 프로그램을 배포할 수 있습니다.", 10);
    private readonly ToolStripStatusLabel _status = new("준비");
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Maximum = 100, Width = 150 };
    private readonly ToolStripDropDownButton _cancel = new("취소") { Visible = false };
    private Catalog _remote = new(), _published = new();
    private string _fingerprint = "";
    private HostIdentity? _identity;
    private CatalogServer? _server;
    private CancellationTokenSource? _operation;
    private bool _busy, _quitting;

    public MainForm(AppState state, bool startInTray)
    {
        _state = state;
        Text = Program.DisplayName;
        Font = new Font("맑은 고딕", 10);
        ForeColor = Ui.Ink;
        BackColor = Ui.Canvas;
        ClientSize = new Size(1110, 740);
        MinimumSize = new Size(900, 650);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(24, 20, 24, 12) };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(Ui.Label("프로그램을 한곳에서.", 22, true), 0, 0);
        var headerButtons = Ui.Bar(Ui.Button("설정", async (_, _) => await SettingsAsync()), Ui.Button("도움말", (_, _) => ShowHelp()));
        headerButtons.WrapContents = false;
        header.Controls.Add(headerButtons, 1, 0);
        header.Controls.Add(_summary, 0, 1);
        header.SetColumnSpan(_summary, 2);
        shell.Controls.Add(header, 0, 0);
        var searchBar = Ui.Bar(Ui.Label("검색", 10, true), _search, Ui.Button("목록 새로고침", async (_, _) => await RefreshAsync()));
        shell.Controls.Add(searchBar, 0, 1);
        shell.Controls.Add(_tabs, 0, 2);
        shell.Controls.Add(_connection, 0, 3);
        _search.TextChanged += (_, _) => Render();
        _cancel.Click += (_, _) => _operation?.Cancel();
        _platform.Items.AddRange([Platforms.Label(Platforms.Modern), Platforms.Label(Platforms.Legacy)]);
        _platform.SelectedIndex = Platforms.Current == Platforms.Modern ? 0 : 1;
        _platform.SelectedIndexChanged += (_, _) => Render();

        _tabs.TabPages.Add(Page("내 프로그램", Ui.Bar(Ui.Button("+ 프로그램 등록", (_, _) => EditLocal(), true), Ui.Button("실행", (_, _) => Launch()), Ui.Button("편집", (_, _) => EditLocal(Selected<LocalProgram>(_local))), Ui.Button("목록에서 제거", (_, _) => RemoveLocal())), _local));
        var catalogPane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        catalogPane.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        catalogPane.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        catalogPane.Controls.Add(_catalog, 0, 0);
        var detailPanel = new GroupBox { Dock = DockStyle.Fill, Text = "설명 및 버전 기록", Padding = new Padding(12, 22, 12, 12) };
        detailPanel.Controls.Add(_details);
        catalogPane.Controls.Add(detailPanel, 0, 1);
        _tabs.TabPages.Add(Page("배포 카탈로그", Ui.Bar(_platform, Ui.Button("설치 / 업데이트", async (_, _) => await InstallAsync(), true), Ui.Button("기존 설치 연결", (_, _) => LinkExisting())), catalogPane));
        _tabs.TabPages.Add(Page("호스트 관리", Ui.Bar(Ui.Button("+ 새 프로그램", async (_, _) => await PublishAsync(null), true), Ui.Button("버전 / Windows 배포본 추가", async (_, _) => await PublishAsync(Selected<CatalogApp>(_host))), Ui.Button("설명 / 이력", (_, _) => ShowHostHistory()), Ui.Button("연결 코드", (_, _) => ShowPairing()), Ui.Button("저장 폴더", (_, _) => OpenFolder(Path.Combine(_state.Root, "repository")))), _host, _hostStatus));
        _host.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowHostHistory(); };
        _local.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Launch(); };
        _local.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Launch(); } };
        _catalog.SelectionChanged += (_, _) => ShowDetails();
        _local.AllowDrop = true;
        _local.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _local.DragDrop += (_, e) =>
        {
            if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
                foreach (var path in paths) EditLocal(initialPath: path);
        };

        var statusStrip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.AddRange([_status, _progress, _cancel]);
        Controls.Add(shell);
        Controls.Add(statusStrip);
        var menu = new ContextMenuStrip();
        menu.Items.Add(Program.DisplayName, null, (_, _) => ShowManager());
        menu.Items.Add("프로그램 목록", null, (_, _) => { _tabs.SelectedIndex = 0; ShowManager(); });
        menu.Items.Add("업데이트 확인", null, async (_, _) => { ShowManager(); _tabs.SelectedIndex = 1; await RefreshAsync(); });
        menu.Items.Add("설정", null, async (_, _) => { ShowManager(); await SettingsAsync(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, async (_, _) => await QuitAsync());
        _tray = new NotifyIcon { Icon = Icon, Text = Program.DisplayName, Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowManager(); };
        FormClosing += (_, e) =>
        {
            if (_quitting || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing) return;
            e.Cancel = true;
            Hide();
        };
        Shown += async (_, _) =>
        {
            if (startInTray) Hide();
            await RunAsync("시작 중…", async _ => { LoadCatalogs(); await RestartHostAsync(); Render(); });
            if (_state.Settings.PairingProtected.Length > 0) await RefreshAsync();
        };
        Render();
    }

    private static TabPage Page(string text, Control toolbar, Control body, Control? note = null)
    {
        var page = new TabPage(text) { BackColor = Ui.Canvas, Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = note is null ? 2 : 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(toolbar, 0, 0);
        if (note != null) { layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.Controls.Add(note, 0, 1); }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(body, 0, note is null ? 1 : 2);
        page.Controls.Add(layout);
        return page;
    }

    private string TargetPlatform => _platform.SelectedIndex == 0 ? Platforms.Modern : Platforms.Legacy;
    private static T? Selected<T>(DataGridView grid) where T : class => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as T : null;
    private bool Matches(string name) => name.IndexOf(_search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0;

    private void LoadCatalogs()
    {
        _published = _state.Store.Read();
        var pair = _state.Pairing;
        _fingerprint = pair?.Fingerprint ?? "";
        _remote = pair != null && _state.Cache.Fingerprint == pair.Fingerprint ? _state.Cache.Catalog : new Catalog();
    }

    private void Render()
    {
        var localId = Selected<LocalProgram>(_local)?.Id;
        var catalogId = Selected<CatalogApp>(_catalog)?.Id;
        var hostId = Selected<CatalogApp>(_host)?.Id;
        _local.Rows.Clear();
        foreach (var item in _state.Settings.Programs.Where(p => Matches(p.Name)).OrderBy(p => p.Name))
        {
            var app = item.HostFingerprint == _fingerprint ? _remote.Apps.FirstOrDefault(a => a.Id == item.CatalogId) : null;
            var latest = app is null ? null : Platforms.Latest(app, Platforms.Current);
            var status = !File.Exists(item.Path) ? "경로 확인 필요" : latest is null ? "등록됨" : InstallStatus(item, latest);
            var row = _local.Rows[_local.Rows.Add(item.Name, item.InstalledVersion.Length > 0 ? item.InstalledVersion : "미확인", status, item.Path)];
            row.Tag = item;
            if (status == "업데이트 가능") row.DefaultCellStyle.ForeColor = Ui.Blue;
        }
        _catalog.Rows.Clear();
        foreach (var app in _remote.Apps.Where(a => Matches(a.Name)).OrderBy(a => a.Name))
        {
            var release = Platforms.Latest(app, TargetPlatform);
            var local = _state.FindInstalled(app.Id, _fingerprint);
            var row = _catalog.Rows[_catalog.Rows.Add(app.Name, local?.InstalledVersion ?? "—", release?.Version ?? "배포본 없음", release is null ? "Windows 배포 대기" : InstallStatus(local, release), app.Description)];
            row.Tag = app;
        }
        _host.Rows.Clear();
        foreach (var app in _published.Apps.Where(a => Matches(a.Name)).OrderBy(a => a.Name))
            _host.Rows[_host.Rows.Add(app.Name, app.Id, Platforms.Latest(app, Platforms.Modern)?.Version ?? "—", Platforms.Latest(app, Platforms.Legacy)?.Version ?? "—", app.Releases.Count)].Tag = app;
        RestoreSelection(_local, localId, x => ((LocalProgram)x).Id);
        RestoreSelection(_catalog, catalogId, x => ((CatalogApp)x).Id);
        RestoreSelection(_host, hostId, x => ((CatalogApp)x).Id);
        var updates = _state.Settings.Programs.Count(p => p.HostFingerprint == _fingerprint && _remote.Apps.Any(a => a.Id == p.CatalogId && Platforms.Latest(a, Platforms.Current) is AppRelease r && InstallStatus(p, r) == "업데이트 가능"));
        _summary.Text = $"내 프로그램 {_state.Settings.Programs.Count}개   ·   업데이트 {updates}개   ·   {Platforms.Label(Platforms.Current)}   ·   {(_state.Settings.HostEnabled ? "호스트 + 클라이언트" : "클라이언트")}";
        _connection.Text = _fingerprint.Length == 0 ? "호스트 미연결 · 내 프로그램은 바로 등록하고 실행할 수 있습니다." : $"마지막 목록 확인: {_state.Cache.CheckedUtc.LocalDateTime:g} · 호스트 연결은 ‘설정’에서 변경할 수 있습니다.";
        ShowDetails();
    }

    private static void RestoreSelection(DataGridView grid, string? id, Func<object, string> getId)
    {
        if (grid.Rows.Count == 0) return;
        var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => r.Tag != null && getId(r.Tag) == id) ?? grid.Rows[0];
        grid.ClearSelection();
        grid.CurrentCell = row.Cells[0];
        row.Selected = true;
    }

    private static string InstallStatus(LocalProgram? local, AppRelease release)
    {
        if (local is null) return "설치 가능";
        if (local.InstalledPlatform.Length > 0 && local.InstalledPlatform != release.Platform) return "다른 Windows용";
        if (!Version.TryParse(local.InstalledVersion, out _)) return "설치 버전 미확인";
        var compare = Platforms.Numeric(local.InstalledVersion).CompareTo(Platforms.Numeric(release.Version));
        return compare < 0 ? "업데이트 가능" : compare == 0 ? "최신" : "로컬 버전이 높음";
    }

    private void ShowDetails()
    {
        var app = Selected<CatalogApp>(_catalog);
        _details.Text = app is null ? "호스트에 연결하면 배포된 프로그램과 변경 내역이 여기에 표시됩니다.\r\n설정에서 연결 코드를 등록한 뒤 목록을 새로고침하세요." : Describe(app);
    }

    private static string Describe(CatalogApp app) => app.Name + "\r\n" + app.Description + "\r\n\r\n" + string.Join("\r\n\r\n", app.Releases.OrderByDescending(r => Platforms.Numeric(r.Version)).ThenBy(r => r.Platform).Select(r => $"v{r.Version}  ·  {Platforms.Label(r.Platform)}  ·  {r.PublishedUtc.LocalDateTime:yyyy-MM-dd}\r\n{r.Notes}\r\n{r.FileName}  ({r.Size / 1048576d:N1} MB)"));

    private void ShowHostHistory()
    {
        var app = Selected<CatalogApp>(_host);
        if (app is null || _busy) return;
        using var dialog = new Form { Text = app.Name + " · 설명 / 이력 · " + Program.DisplayName, Font = Font, ClientSize = new Size(720, 520), StartPosition = FormStartPosition.CenterParent, Padding = new Padding(16), BackColor = Color.White };
        dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = Describe(app), BackColor = Color.White, BorderStyle = BorderStyle.None });
        dialog.ShowDialog(this);
    }

    private void EditLocal(LocalProgram? item = null, string? initialPath = null)
    {
        if (_busy) return;
        try
        {
            var result = Dialogs.EditLocal(this, item, initialPath: initialPath);
            if (result != null) SaveLocal(result);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveLocal(LocalProgram item)
    {
        _state.SaveProgram(item);
        Render();
    }

    private void Launch()
    {
        var item = Selected<LocalProgram>(_local);
        if (item is null) return;
        try { AppState.Launch(item); _status.Text = item.Name + " 실행 요청 완료"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RemoveLocal()
    {
        var item = Selected<LocalProgram>(_local);
        if (item is null || _busy) return;
        if (MessageBox.Show(this, $"‘{item.Name}’을 관리 목록에서 제거할까요?\n설치된 프로그램과 바로가기 파일은 유지됩니다.", Program.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var before = _state.Settings.Programs.ToList();
        try { _state.Settings.Programs.Remove(item); _state.Save(); Render(); }
        catch (Exception ex) { _state.Settings.Programs = before; ShowError(ex); }
    }

    private async Task RefreshAsync()
    {
        await RunAsync("배포 목록 확인 중…", async token =>
        {
            _published = await Task.Run(() => _state.Store.Read(), token);
            var info = _state.Pairing;
            if (info is null) { Render(); _status.Text = "호스트 미연결 · 설정에서 연결 코드를 등록하세요."; return; }
            using var client = new CatalogClient(info);
            var catalog = await client.FetchCatalogAsync(token);
            _state.Cache = new CachedCatalog { Catalog = catalog, CheckedUtc = DateTimeOffset.UtcNow, Fingerprint = info.Fingerprint };
            _state.SaveCache();
            _remote = catalog;
            _fingerprint = info.Fingerprint;
            Render();
            _status.Text = $"배포 목록 {_remote.Apps.Count}개 확인 완료";
        });
    }

    private async Task PublishAsync(CatalogApp? app)
    {
        if (_busy) return;
        if (!_state.Settings.HostEnabled) { _status.Text = "설정에서 호스트 역할을 켜세요."; return; }
        var input = Dialogs.Publish(this, app);
        if (input is null) return;
        await RunAsync("설치 파일 등록 및 무결성 계산 중…", async token =>
        {
            _published = await Task.Run(() => _state.Store.PublishAsync(input.Id, input.Name, input.Description, input.Version, input.Notes, input.Path, input.Platform, token), token);
            Render();
            _status.Text = $"{input.Name} {input.Version} · {Platforms.Label(input.Platform)} 배포 등록 완료";
        });
    }

    private void ShowPairing()
    {
        if (_busy) return;
        if (_identity is null || _server is null) { _status.Text = "설정에서 호스트 역할을 켜고 수신을 시작하세요."; return; }
        try
        {
            var info = new PairingInfo { Host = _state.Settings.AdvertisedHost, Port = _state.Settings.Port, Fingerprint = _identity.Fingerprint, Token = _identity.Token };
            Dialogs.ShowPairing(this, info.Export(), info.Host + ":" + info.Port);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LinkExisting()
    {
        if (_busy) return;
        var app = Selected<CatalogApp>(_catalog);
        if (app is null) return;
        if (TargetPlatform != Platforms.Current) { _status.Text = "이 PC에 맞는 대상 Windows를 선택하세요."; return; }
        var existing = _state.FindInstalled(app.Id, _fingerprint);
        // Existing installations need a user-verified version; never assume the catalog version is installed.
        var result = Dialogs.EditLocal(this, existing, app: app, fingerprint: _fingerprint);
        if (result != null)
        {
            result.InstalledPlatform = TargetPlatform;
            try { SaveLocal(result); } catch (Exception ex) { ShowError(ex); }
        }
    }

    private async Task InstallAsync()
    {
        var selected = Selected<CatalogApp>(_catalog);
        if (_busy || selected is null) return;
        var release = Platforms.Latest(selected, TargetPlatform);
        if (release is null) { _status.Text = "선택한 Windows용 배포본이 없습니다."; return; }
        if (TargetPlatform != Platforms.Current) { _status.Text = "다른 Windows용 설치 파일입니다. 이 PC에 맞는 항목을 선택하세요."; return; }
        var old = _state.FindInstalled(selected.Id, _fingerprint);
        if (Version.TryParse(old?.InstalledVersion, out _) && Platforms.Numeric(old!.InstalledVersion) > Platforms.Numeric(release.Version))
        { _status.Text = "현재 설치 버전이 더 높습니다. 자동 다운그레이드는 진행하지 않습니다."; return; }
        if (MessageBox.Show(this, $"{selected.Name} {release.Version}\n{Platforms.Label(release.Platform)} · {release.Size / 1048576d:N1} MB\n\n{release.Notes}\n\n다운로드 후 설치 프로그램을 실행할까요?", "설치 / 업데이트 · " + Program.DisplayName, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        await RunAsync("설치 파일 다운로드 중…", async token =>
        {
            var info = _state.Pairing ?? throw new InvalidOperationException("호스트 연결이 필요합니다.");
            using var client = new CatalogClient(info);
            var fresh = await client.FetchCatalogAsync(token);
            var app = fresh.Apps.Single(a => a.Id == selected.Id);
            var verifiedRelease = app.Releases.Single(r => r.Platform == release.Platform && Platforms.Numeric(r.Version) == Platforms.Numeric(release.Version));
            if (verifiedRelease.Sha256 != release.Sha256 || verifiedRelease.Size != release.Size) throw new InvalidDataException("배포 파일 정보가 변경되었습니다. 목록을 새로고침한 후 다시 확인하세요.");
            var package = await client.DownloadAsync(app, verifiedRelease, _state.Downloads, new Progress<int>(p => _progress.Value = Math.Max(0, Math.Min(100, p))), token);
            token.ThrowIfCancellationRequested();
            _cancel.Visible = false;
            _status.Text = "설치 프로그램 진행 중 · 설치 창에서 완료해 주세요.";
            var start = Path.GetExtension(package).Equals(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"), "/i \"" + package + "\"") { UseShellExecute = true }
                : new ProcessStartInfo(package) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(package)! };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("설치 프로그램을 시작하지 못했습니다.");
            await Task.Run(() => process.WaitForExit());
            int exit = process.ExitCode;
            if (exit != 0 && exit != 3010) throw new InvalidOperationException($"설치 프로그램이 완료되지 않았습니다 (종료 코드 {exit}). 기존 설치 버전 기록을 유지합니다.");
            ShowManager();
            var result = Dialogs.EditLocal(this, old, app, verifiedRelease, info.Fingerprint);
            if (result != null) { SaveLocal(result); _status.Text = app.Name + " " + verifiedRelease.Version + " 설치 완료 기록" + (exit == 3010 ? " · Windows 재시작 필요" : ""); }
            else _status.Text = "설치 확인을 취소했습니다. 기존 설치 버전 기록을 유지합니다.";
        });
    }

    private async Task SettingsAsync()
    {
        if (_busy) return;
        var change = Dialogs.Settings(this, _state);
        if (change is null) return;
        await RunAsync("설정 적용 중…", async token =>
        {
            string protectedPairing = _state.Settings.PairingProtected;
            CachedCatalog? newCache = null;
            if (change.NewPairing.Length > 0)
            {
                var info = PairingInfo.Parse(change.NewPairing);
                using var client = new CatalogClient(info);
                var remote = await client.FetchCatalogAsync(token);
                protectedPairing = Secrets.Protect(change.NewPairing);
                newCache = new CachedCatalog { Catalog = remote, CheckedUtc = DateTimeOffset.UtcNow, Fingerprint = info.Fingerprint };
            }
            if (change.Disconnect) protectedPairing = "";
            var next = new UserSettings { HostEnabled = change.HostEnabled, Port = change.Port, AdvertisedHost = change.Host, AutoStart = change.AutoStart, PairingProtected = protectedPairing, Programs = _state.Settings.Programs };
            _state.CommitSettings(next);
            try
            {
                if (newCache != null) { _state.Cache = newCache; _state.SaveCache(); }
                LoadCatalogs();
                await RestartHostAsync();
            }
            finally { LoadCatalogs(); Render(); }
            _status.Text = "설정 저장 완료";
        });
    }

    private async Task RestartHostAsync()
    {
        if (_server != null) { await _server.StopAsync(); _server.Dispose(); _server = null; }
        if (!_state.Settings.HostEnabled) { _hostStatus.Text = "호스트 꺼짐 · 설정에서 호스트 역할을 선택하세요."; return; }
        _identity ??= await Task.Run(() => HostIdentity.LoadOrCreate(Path.Combine(_state.Root, "identity")));
        var server = new CatalogServer(_state.Store, _identity);
        try { await server.StartAsync(_state.Settings.Port); _server = server; }
        catch { server.Dispose(); _hostStatus.Text = "호스트 시작 실패 · 포트 사용 여부와 설정을 확인하세요."; throw; }
        _hostStatus.Text = $"배포 수신 중 · {_state.Settings.AdvertisedHost}:{_state.Settings.Port} · 연결 코드로 클라이언트를 등록하세요.";
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        _tabs.Enabled = false;
        _search.Enabled = false;
        _operation = new CancellationTokenSource();
        _progress.Value = 0;
        _progress.Visible = _cancel.Visible = true;
        _status.Text = message;
        try { await work(_operation.Token); if (_status.Text == message) _status.Text = "준비 완료"; }
        catch (OperationCanceledException) { _status.Text = "작업이 취소되었습니다."; }
        catch (Exception) when (_operation.IsCancellationRequested) { _status.Text = "작업이 취소되었습니다."; }
        catch (Exception ex) { _status.Text = "작업 실패 · 기존 프로그램은 계속 실행할 수 있습니다."; ShowError(ex); }
        finally
        {
            _operation.Dispose(); _operation = null;
            _busy = false;
            _tabs.Enabled = _search.Enabled = true;
            _progress.Visible = _cancel.Visible = false;
        }
    }

    private async Task QuitAsync()
    {
        if (_busy) { ShowManager(); _status.Text = "진행 중인 작업을 완료하거나 취소한 뒤 종료하세요."; return; }
        _quitting = true;
        if (_server != null) await _server.StopAsync();
        Close();
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    private void ShowManager() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ShowHelp()
    {
        try { Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "guide.html")) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }
    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == Program.ShowMessage) ShowManager();
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tray.Visible = false; _tray.Dispose(); _server?.Dispose(); _identity?.Dispose(); }
        base.Dispose(disposing);
    }
}
