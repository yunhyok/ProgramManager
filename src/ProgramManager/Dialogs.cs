using ProgramManager.Core;
using System.Diagnostics;

namespace ProgramManager;

internal static class Dialogs
{
    public static void ShowHtml(IWin32Window owner, string path, string section = "")
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("설명 파일을 찾을 수 없습니다.", path);
        using var form = new Form();
        Ui.BeginForm(form);
        form.Text = "도움말 / 프로그램 설명 · " + Program.DisplayName;
        form.ClientSize = new Size(1000, 720);
        form.MinimumSize = new Size(560, 400);
        form.StartPosition = FormStartPosition.CenterParent;
        var browser = new WebBrowser { Dock = DockStyle.Fill, AllowWebBrowserDrop = false, IsWebBrowserContextMenuEnabled = false, ScriptErrorsSuppressed = true };
        browser.Navigating += (_, e) => e.Cancel = e.Url is null || !e.Url.IsFile || !string.Equals(e.Url.LocalPath, path, StringComparison.OrdinalIgnoreCase);
        browser.NewWindow += (_, e) => e.Cancel = true;
        void FitDocument()
        {
            if (browser.Document?.Body is not { } body) return;
            using var graphics = form.CreateGraphics();
            body.Style = "zoom: " + Math.Round(graphics.DpiX / 96 * form.Font.Size / 10 * 100).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%;";
            if (section.Length > 0) browser.Document.GetElementById(section)?.ScrollIntoView(true);
            body.ScrollLeft = 0;
            var html = browser.Document.GetElementsByTagName("html");
            if (html.Count > 0 && html[0] is { } documentRoot) documentRoot.ScrollLeft = 0;
        }
        browser.DocumentCompleted += (_, _) => FitDocument();
        form.FontChanged += (_, _) => FitDocument();
        form.DpiChanged += (_, _) => FitDocument();
        var close = Ui.Button("닫기", (_, _) => form.Close());
        var footer = Ui.Bar(close); footer.Dock = DockStyle.Bottom;
        form.Controls.Add(browser); form.Controls.Add(footer); form.CancelButton = close;
        form.ResumeLayout(true);
        form.Shown += (_, _) => browser.Navigate(new Uri(path).AbsoluteUri + (section.Length == 0 ? "" : "#" + section));
        form.ShowDialog(owner);
    }

    internal sealed class Fields : Form
    {
        private readonly TableLayoutPanel _table;
        public Fields(string title, int height = 450)
        {
            Ui.BeginForm(this);
            Text = title + " · " + Program.DisplayName;
            ForeColor = Ui.Ink;
            BackColor = Color.White;
            ClientSize = new Size(620, height);
            MinimumSize = new Size(560, 350);
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = MaximizeBox = false;
            _table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(22) };
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_table);
        }
        protected override void OnLoad(EventArgs e)
        {
            ResumeLayout(true);
            base.OnLoad(e);
        }
        public void Row(string name, Control control)
        {
            int row = _table.RowCount++;
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = Ui.Label(name, 9);
            label.Margin = new Padding(0, 9, 10, 10);
            _table.Controls.Add(label, 0, row);
            control.Dock = DockStyle.Fill;
            control.Margin = new Padding(0, 4, 0, 8);
            _table.Controls.Add(control, 1, row);
        }
        public TextBox TextField(string name, string value = "", bool multi = false)
        {
            var box = new TextBox { Text = value, Multiline = multi, Height = multi ? 74 : 30, ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None };
            Row(name, box);
            return box;
        }
        public TextBox FileField(string name, string path, string filter)
        {
            var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var box = new TextBox { Text = path, Dock = DockStyle.Fill };
            var browse = Ui.Button("찾기…", (_, _) =>
            {
                using var picker = new OpenFileDialog { Filter = filter, CheckFileExists = true };
                if (picker.ShowDialog(this) == DialogResult.OK) box.Text = picker.FileName;
            });
            panel.Controls.Add(box);
            panel.Controls.Add(browse);
            Row(name, panel);
            return box;
        }
        public void Finish(string text, Action validate)
        {
            var save = Ui.Button(text, (_, _) =>
            {
                try { validate(); DialogResult = DialogResult.OK; }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }, true);
            var cancel = Ui.Button("취소", (_, _) => DialogResult = DialogResult.Cancel);
            Actions(save, cancel);
            AcceptButton = save;
            CancelButton = cancel;
        }
        public void Actions(params Control[] buttons)
        {
            var bar = Ui.Bar(buttons);
            bar.Dock = DockStyle.Bottom;
            bar.FlowDirection = FlowDirection.RightToLeft;
            bar.Padding = new Padding(22, 10, 22, 14);
            Controls.Add(bar);
        }
    }

    public static LocalProgram? EditLocal(IWin32Window owner, LocalProgram? item = null, CatalogApp? app = null, AppRelease? release = null, string fingerprint = "", string? initialPath = null)
    {
        using var form = new Fields(release is null ? "프로그램 등록 / 편집" : "설치 결과 확인", 465);
        var name = form.TextField("프로그램 이름", item?.Name ?? app?.Name ?? "");
        var path = form.FileField("실행 파일 / 바로가기", initialPath ?? item?.Path ?? "", "프로그램 및 바로가기|*.exe;*.lnk");
        var version = form.TextField("설치된 버전", release?.Version ?? item?.InstalledVersion ?? "");
        path.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(path.Text);
            if (string.IsNullOrWhiteSpace(version.Text) && File.Exists(path.Text) && Path.GetExtension(path.Text).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(path.Text);
                    if (info.FileMajorPart > 0 || info.FileMinorPart > 0) version.Text = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
                }
                catch { /* Optional file metadata does not affect launch registration. */ }
            }
        };
        form.Row("안내", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = release is null ? "버전을 모르면 비워 두세요. 바로가기는 Manager 폴더에 복사해 보관합니다. 등록 후 실행을 확인했다면 바탕화면 바로가기를 정리해도 됩니다." : "설치 프로그램에서 설치가 완료되었는지 확인한 뒤, 설치된 프로그램의 실행 파일을 선택하세요. 저장할 때 이 버전을 설치 완료로 기록합니다." });
        LocalProgram? result = null;
        form.Finish(release is null ? "저장" : "설치 완료 기록", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidDataException("프로그램 이름을 입력하세요.");
            AppState.ValidateLaunchPath(path.Text.Trim());
            if (version.Text.Trim().Length > 0) Platforms.Numeric(version.Text.Trim());
            result = new LocalProgram { Id = item?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), Path = path.Text.Trim(), InstalledVersion = version.Text.Trim(), CatalogId = app?.Id ?? item?.CatalogId ?? "", HostFingerprint = app is null ? item?.HostFingerprint ?? "" : fingerprint, InstalledPlatform = release?.Platform ?? item?.InstalledPlatform ?? "" };
        });
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    public sealed class SettingsChange
    {
        public bool HostEnabled, AutoStart, Disconnect, ManagerAutoCheck;
        public int Port;
        public string Host = "", NewPairing = "";
    }

    public static void Settings(IWin32Window owner, AppState state, string selectedTab = "general",
        Func<SettingsChange, CancellationToken, Task>? apply = null, Func<string>? createCode = null,
        Func<IWin32Window, Task>? githubSettings = null)
    {
        using var form = new Form();
        Ui.BeginForm(form);
        form.Text = "설정 · " + Program.DisplayName;
        form.ClientSize = new Size(740, 680);
        form.MinimumSize = new Size(560, 420);
        form.StartPosition = FormStartPosition.CenterParent;
        form.BackColor = Color.White;
        form.ForeColor = Ui.Ink;
        var tabs = new TabControl { Name = "SettingsTabs", Dock = DockStyle.Fill, Padding = new Point(16, 10) };
        TableLayoutPanel Page(string title)
        {
            var page = new TabPage(title) { BackColor = Color.White, AutoScroll = true };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(20) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(table); tabs.TabPages.Add(page); return table;
        }
        void Row(TableLayoutPanel table, string name, Control control)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = Ui.Label(name, 9);
            label.MaximumSize = new Size(155, 0); label.Margin = new Padding(0, 8, 12, 8);
            table.Controls.Add(label, 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(0, 4, 0, 10);
            table.Controls.Add(control, 1, row);
        }
        Label Note(TableLayoutPanel table, string text, bool bold = false)
        {
            var label = Ui.Label(text, 10, bold);
            label.Dock = DockStyle.Fill; label.Margin = new Padding(0, 6, 0, 14);
            var row = table.RowCount++;
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(label, 0, row); table.SetColumnSpan(label, 2); return label;
        }
        var general = Page("일반");
        var sending = Page("호스트 · 배포");
        var receiving = Page("클라이언트 · 연결");
        Note(general, "이 PC에서 어떤 일을 할까요?", true);
        Note(general, "모든 PC에서 프로그램을 등록하고 실행할 수 있습니다. 다른 PC에 프로그램을 제공하려면 호스트 역할을 켭니다.");
        var role = new CheckBox { Name = "HostRole", Text = "다른 PC에 프로그램 배포 (호스트)", AutoSize = true, Checked = state.Settings.HostEnabled };
        Row(general, "이 PC의 역할", role);
        Note(general, "호스트: ‘호스트 · 배포’에서 주소와 연결 코드를 준비합니다.\n클라이언트: ‘클라이언트 · 연결’에서 호스트가 준 코드를 등록합니다. 호스트 PC도 다른 호스트에 연결할 수 있습니다.");
        var auto = new CheckBox { Text = "로그인 시 자동 실행", AutoSize = true, Checked = state.Settings.AutoStart };
        Row(general, "Windows 시작", auto);
        var managerAuto = new CheckBox { Text = "시작 시 / 6시간마다 새 버전 확인", AutoSize = true, Checked = state.Settings.ManagerAutoCheck };
        Row(general, "관리 프로그램 업데이트", managerAuto);

        var hostIntro = Note(sending, "", true);
        Note(sending, "① 이 PC 주소 확인 → ② 연결 코드 복사 → ③ 받는 PC에 전달\n배포할 앱은 메인 화면 ‘호스트 관리’에서 선택합니다.");
        var host = new ComboBox { Name = "HostAddress", AccessibleName = "이 호스트 PC의 내부망 주소", DropDownStyle = ComboBoxStyle.DropDown };
        var addresses = ConnectionSettings.LocalAddresses();
        host.Items.AddRange(addresses.Cast<object>().ToArray());
        host.Text = state.Settings.AdvertisedHost;
        Row(sending, "이 호스트 PC의 주소", host);
        Note(sending, "보내는 PC의 내부망 IP를 목록에서 고르세요. 내부망에서 찾을 수 있는 이 PC의 이름도 사용할 수 있습니다.");
        var port = new NumericUpDown { Name = "HostPort", Minimum = 1024, Maximum = 65535, Value = state.Settings.Port };
        Row(sending, "호스트 수신 포트", port);
        Note(sending, "기본값 45672를 사용하세요. 주소와 포트는 연결 코드에 자동으로 포함됩니다.");
        var github = Ui.Button("GitHub 및 임시 파일 설정", async (_, _) => { if (githubSettings != null) await githubSettings(form); });
        Row(sending, "배포 파일 가져오기", github);
        var outgoing = new TextBox { Name = "GeneratedConnectionCode", AccessibleName = "자동 생성된 연결 코드", Multiline = true, ReadOnly = true, Height = 70, ScrollBars = ScrollBars.Vertical };
        Row(sending, "자동 생성 연결 코드", outgoing);
        Note(sending, "버튼을 누르면 설정 적용과 접속 확인 후 코드를 자동 생성합니다. 복사한 PM1: 코드 전체를 받는 PC의 ‘설정 → 클라이언트 · 연결’에 붙여 넣으세요.");

        Note(receiving, "받는 PC에서 호스트에 연결하기", true);
        Note(receiving, "호스트 PC에서 ‘설정 → 호스트 · 배포 → 연결 코드 생성 · 복사’를 누른 후 그 코드를 가져오세요. 클라이언트는 IP·포트나 GitHub 계정을 따로 입력하지 않습니다.");
        var current = Ui.Label("", 10);
        Row(receiving, "현재 연결", current);
        var incoming = new TextBox { Name = "ReceivedConnectionCode", AccessibleName = "호스트에서 받은 연결 코드", Multiline = true, Height = 85, ScrollBars = ScrollBars.Vertical };
        Row(receiving, "호스트에서 받은 연결 코드", incoming);
        var preview = Ui.Label("PM1:으로 시작하는 코드 전체를 붙여 넣으세요. 비워 두면 기존 연결을 유지합니다.", 9);
        Row(receiving, "연결할 대상", preview);
        var disconnect = new CheckBox { Name = "DisconnectHost", Text = "현재 호스트 연결 해제", AutoSize = true };
        Row(receiving, "연결 해제", disconnect);
        Note(receiving, "‘연결 확인’은 목록을 받아볼 수 있는지 검사합니다. ‘설정 적용’을 누르면 연결을 저장하고 배포 목록을 바로 표시합니다. 배포 목록의 앱을 설치해야 ‘내 프로그램’에 등록할 수 있습니다.");

        var status = Ui.Label("변경한 옵션은 ‘설정 적용’을 눌러 저장하세요.", 9);
        status.Name = "ConnectionSettingsStatus";
        var progress = new ProgressBar { Style = ProgressBarStyle.Marquee, Visible = false, Dock = DockStyle.Top, Height = 8 };
        CancellationTokenSource? operation = null;
        Button save = null!, close = null!, generate = null!;
        save = Ui.Button("설정 적용", async (_, _) => await ApplyAsync(), true);
        close = Ui.Button("닫기", (_, _) => { if (operation != null) operation.Cancel(); else form.Close(); });
        generate = Ui.Button("연결 코드 생성 · 복사", async (_, _) => await RunAsync(async token =>
        {
            if (apply is null || createCode is null) throw new InvalidOperationException("실행 중인 호스트에서 연결 코드를 생성하세요.");
            await apply(ReadChange(), token);
            incoming.Clear(); disconnect.Checked = false; RefreshCurrent();
            var code = createCode();
            var catalog = await ConnectionSettings.CheckAsync(code, token);
            outgoing.Text = code; Clipboard.SetText(code);
            status.Text = $"호스트 접속 확인 완료 · 배포 앱 {catalog.Apps.Count}개 · 연결 코드를 복사했습니다. 받는 PC의 클라이언트 탭에 붙여 넣으세요.";
            RefreshCurrent();
        }));
        generate.Name = "GenerateConnectionCode";
        Row(sending, "받는 PC에 전달", generate);
        var test = Ui.Button("연결 확인", async (_, _) => await RunAsync(async token =>
        {
            var code = incoming.Text.Trim();
            if (code.Length == 0) code = state.Pairing?.Export() ?? "";
            var catalog = await ConnectionSettings.CheckAsync(code, token);
            status.Text = $"연결 성공 · 배포 앱 {catalog.Apps.Count}개. ‘설정 적용’을 눌러 이 연결을 저장하세요.";
        }));
        test.Name = "TestHostConnection";
        Row(receiving, "저장 전 확인", test);
        void RefreshRole()
        {
            hostIntro.Text = role.Checked ? "호스트 역할 · 이 PC의 프로그램을 다른 PC에 제공합니다." : "호스트 역할이 꺼져 있습니다. ‘일반’ 탭에서 배포 역할을 켜면 사용할 수 있습니다.";
            host.Enabled = port.Enabled = github.Enabled = outgoing.Enabled = generate.Enabled = role.Checked && operation is null;
        }
        void RefreshCurrent() => current.Text = state.Pairing is PairingInfo pair ? pair.Host + ":" + pair.Port + " · 저장된 호스트" : "연결된 호스트 없음";
        SettingsChange ReadChange()
        {
            if (role.Checked && string.IsNullOrWhiteSpace(host.Text)) throw new InvalidDataException("호스트 탭에서 이 PC의 내부망 IP 또는 PC 이름을 입력하세요.");
            if (disconnect.Checked && incoming.Text.Trim().Length > 0) throw new InvalidDataException("새 호스트 연결 또는 기존 연결 해제 중 하나만 선택하세요.");
            return new SettingsChange { HostEnabled = role.Checked, Host = host.Text.Trim().Length == 0 ? state.Settings.AdvertisedHost : host.Text.Trim(), Port = (int)port.Value,
                AutoStart = auto.Checked, ManagerAutoCheck = managerAuto.Checked, NewPairing = incoming.Text.Trim(), Disconnect = disconnect.Checked };
        }
        async Task ApplyAsync() => await RunAsync(async token =>
        {
            if (apply is null) return;
            await apply(ReadChange(), token);
            incoming.Clear(); disconnect.Checked = false; RefreshCurrent();
            status.Text = state.Pairing is null ? "설정을 적용했습니다. 호스트 탭에서 연결 코드를 생성하거나 클라이언트 탭에서 받은 코드를 등록하세요." : $"설정 적용 완료 · {state.Pairing.Host}:{state.Pairing.Port} · 배포 앱 {state.Cache.Catalog.Apps.Count}개를 불러왔습니다.";
        });
        async Task RunAsync(Func<CancellationToken, Task> work)
        {
            if (operation != null) return;
            operation = new CancellationTokenSource();
            tabs.Enabled = save.Enabled = false; progress.Visible = true; close.Text = "취소";
            status.Text = "호스트 연결을 확인하고 있습니다…";
            try { await work(operation.Token); }
            catch (OperationCanceledException) { status.Text = "연결 확인을 취소했습니다."; }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { operation.Dispose(); operation = null; tabs.Enabled = save.Enabled = true; progress.Visible = false; close.Text = "닫기"; RefreshRole(); }
        }
        role.CheckedChanged += (_, _) => { outgoing.Clear(); RefreshRole(); };
        host.TextChanged += (_, _) => outgoing.Clear(); port.ValueChanged += (_, _) => outgoing.Clear();
        incoming.TextChanged += (_, _) =>
        {
            if (incoming.Text.Trim().Length == 0) { preview.Text = "PM1:으로 시작하는 코드 전체를 붙여 넣으세요. 비워 두면 기존 연결을 유지합니다."; return; }
            try { var pair = PairingInfo.Parse(incoming.Text.Trim()); preview.Text = pair.Host + ":" + pair.Port + " · 코드에 포함된 호스트 주소"; }
            catch { preview.Text = "연결 코드 형식이 아닙니다. IP나 임의의 번호 대신 호스트가 자동 생성한 PM1: 코드 전체를 붙여 넣으세요."; }
        };
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 8, 20, 12) };
        status.Dock = DockStyle.Fill;
        footer.Controls.Add(progress); footer.Controls.Add(status);
        var actions = Ui.Bar(save, close); actions.FlowDirection = FlowDirection.RightToLeft;
        footer.Controls.Add(actions);
        form.Controls.Add(tabs); form.Controls.Add(footer);
        tabs.SelectedIndex = selectedTab == "host" ? 1 : selectedTab == "client" ? 2 : 0;
        form.CancelButton = close;
        form.FormClosing += (_, e) => { if (operation != null) { e.Cancel = true; operation.Cancel(); } };
        RefreshCurrent(); RefreshRole(); form.ResumeLayout(true);
        form.ShowDialog(owner);
    }
}
