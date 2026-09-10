using ProgramManager.Core;
using System.Diagnostics;

namespace ProgramManager;

internal static class Dialogs
{
    private sealed class Fields : Form
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

    public sealed class Publication
    {
        public string Id = "", Name = "", Description = "", Version = "", Notes = "", Path = "", Platform = "";
    }

    public static Publication? Publish(IWin32Window owner, CatalogApp? app)
    {
        using var form = new Fields(app is null ? "새 프로그램 배포" : "버전 / Windows 배포본 추가", 720);
        var id = form.TextField("프로그램 ID", app?.Id ?? "");
        id.ReadOnly = app != null;
        var name = form.TextField("프로그램 이름", app?.Name ?? "");
        var description = form.TextField("설명", app?.Description ?? "", true);
        var version = form.TextField("버전", app?.Latest?.Version ?? "0.1.0");
        var platform = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        platform.Items.AddRange([Platforms.Label(Platforms.Modern), Platforms.Label(Platforms.Legacy)]);
        platform.SelectedIndex = 0;
        form.Row("대상 Windows", platform);
        var path = form.FileField("설치 파일", "", "Windows 설치 파일|*.exe;*.msi");
        var notes = form.TextField("변경 내역", "", true);
        form.Row("안내", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = "ID 예: intra-drop. 같은 프로그램은 ID를 유지하세요. 같은 버전도 대상 Windows가 다르면 각각 등록할 수 있습니다. 배포된 파일은 덮어쓰지 않습니다." });
        Publication? result = null;
        form.Finish("배포본 등록", () =>
        {
            if (!File.Exists(path.Text)) throw new FileNotFoundException("설치 파일을 선택하세요.");
            Platforms.Numeric(version.Text.Trim());
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(id.Text)) throw new InvalidDataException("프로그램 ID와 이름을 입력하세요.");
            result = new Publication { Id = id.Text.Trim(), Name = name.Text.Trim(), Description = description.Text.Trim(), Version = version.Text.Trim(), Notes = notes.Text.Trim(), Path = path.Text, Platform = platform.SelectedIndex == 0 ? Platforms.Modern : Platforms.Legacy };
        });
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    public sealed class SettingsChange
    {
        public bool HostEnabled, AutoStart, Disconnect;
        public int Port;
        public string Host = "", NewPairing = "";
    }

    public static SettingsChange? Settings(IWin32Window owner, AppState state)
    {
        using var form = new Fields("설정", 590);
        var role = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        role.Items.AddRange(["클라이언트 — 실행 / 설치 / 업데이트", "호스트 + 클라이언트 — 배포 관리 포함"]);
        role.SelectedIndex = state.Settings.HostEnabled ? 1 : 0;
        form.Row("이 PC의 역할", role);
        var host = form.TextField("이 PC의 배포 주소", state.Settings.AdvertisedHost);
        var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = state.Settings.Port };
        form.Row("호스트 수신 포트", port);
        host.Enabled = port.Enabled = state.Settings.HostEnabled;
        role.SelectedIndexChanged += (_, _) => host.Enabled = port.Enabled = role.SelectedIndex == 1;
        var auto = new CheckBox { Text = "로그인 시 자동 실행", AutoSize = true, Checked = state.Settings.AutoStart };
        form.Row("시작 옵션", auto);
        form.Row("연결된 배포 호스트", Ui.Label(state.Pairing is PairingInfo current ? current.Host + ":" + current.Port : "연결 안 됨", 9));
        var pairing = form.TextField("새 호스트 연결 코드", "", true);
        form.Row("연결 방법", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = "배포 호스트의 ‘연결 코드’에서 복사한 코드를 붙여 넣으세요. 비워 두면 기존 연결을 유지합니다. 코드는 신뢰하는 PC에만 전달하세요." });
        var disconnect = new CheckBox { Text = "호스트 연결 해제", AutoSize = true };
        form.Row("", disconnect);
        SettingsChange? result = null;
        form.Finish("설정 저장", () =>
        {
            if (string.IsNullOrWhiteSpace(host.Text)) throw new InvalidDataException("호스트 PC 이름 또는 IP를 입력하세요.");
            if (pairing.Text.Trim().Length > 0) PairingInfo.Parse(pairing.Text.Trim());
            if (disconnect.Checked && pairing.Text.Trim().Length > 0) throw new InvalidDataException("새 연결 또는 연결 해제 중 하나만 선택하세요.");
            result = new SettingsChange { HostEnabled = role.SelectedIndex == 1, Host = host.Text.Trim(), Port = (int)port.Value, AutoStart = auto.Checked, NewPairing = pairing.Text.Trim(), Disconnect = disconnect.Checked };
        });
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    public static void ShowPairing(IWin32Window owner, string code, string address)
    {
        using var form = new Fields("클라이언트 연결 코드", 360);
        form.Row("호스트", Ui.Label(address));
        var box = form.TextField("연결 코드", code, true);
        box.ReadOnly = true;
        form.Row("안내", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = "클라이언트의 설정에 이 코드를 붙여 넣으세요. 이 코드를 가진 PC는 배포 목록과 설치 파일을 받을 수 있습니다." });
        form.Actions(Ui.Button("코드 복사", (_, _) => { Clipboard.SetText(code); }), Ui.Button("닫기", (_, _) => form.Close()));
        form.ShowDialog(owner);
    }
}
