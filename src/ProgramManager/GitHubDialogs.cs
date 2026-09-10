using ProgramManager.Core;

namespace ProgramManager;

internal static class GitHubDialogs
{
    internal sealed class SettingsChange
    {
        public string Owner = "", Token = "";
        public int AuthMode, RetentionDays;
    }

    public static SettingsChange? Settings(IWin32Window owner, UserSettings current)
    {
        using var form = new Dialogs.Fields("GitHub 설정", 600);
        form.Row("사용 순서", Ui.Label("1. GitHub 연결 설정 → 2. 저장소 선택 → 3. 클라이언트에 연결 코드 전달", 9));
        var account = form.TextField("GitHub 계정 / 조직", current.GitHubOwner);
        var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.AddRange(["기존 GitHub CLI 로그인 사용", "토큰 직접 입력 (비공개 저장소 포함)", "공개 저장소만 (로그인 없음)"]);
        mode.SelectedIndex = current.GitHubTokenProtected.Length > 0 ? 1 : current.UseGitHubCli ? 0 : 2;
        form.Row("로그인 방식", mode);
        var credential = form.TextField("GitHub 토큰");
        credential.UseSystemPasswordChar = true;
        credential.Enabled = mode.SelectedIndex == 1;
        mode.SelectedIndexChanged += (_, _) => credential.Enabled = mode.SelectedIndex == 1;
        form.Row("토큰 안내", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = current.GitHubTokenProtected.Length > 0 ? "저장된 토큰이 있습니다. 토큰 방식을 유지하고 비워 두면 기존 값을 사용합니다. 토큰은 이 PC에만 암호화하여 보관합니다." : "토큰 방식은 선택한 저장소의 Contents 읽기 권한이 필요합니다. 클라이언트에는 GitHub 로그인이나 토큰이 필요하지 않습니다." });
        var days = new NumericUpDown { Minimum = 1, Maximum = 30, Value = current.CacheRetentionDays };
        form.Row("임시 파일 보관일", days);
        form.Row("정리 기준", new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = "마지막으로 사용한 뒤 이 기간이 지난 설치 파일·설명 캐시를 정리합니다. 프로그램 시작 시와 실행 중 매시간 확인하며, 전송·설치 중인 파일은 보관합니다." });
        SettingsChange? result = null;
        form.Finish("GitHub 설정 저장", () =>
        {
            if (mode.SelectedIndex == 2 && account.Text.Trim().Length == 0) throw new InvalidDataException("공개 저장소 방식에서는 GitHub 계정 또는 조직을 입력하세요.");
            if (mode.SelectedIndex == 1 && credential.Text.Trim().Length == 0 && current.GitHubTokenProtected.Length == 0) throw new InvalidDataException("GitHub 토큰을 입력하세요.");
            result = new SettingsChange { Owner = account.Text.Trim(), AuthMode = mode.SelectedIndex, Token = credential.Text.Trim(), RetentionDays = (int)days.Value };
        });
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }

    public static List<GitHubSelection>? Sources(IWin32Window owner, string account, List<GitHubRepository> repositories, List<GitHubSelection> current)
    {
        using var form = new Form();
        Ui.BeginForm(form);
        form.Text = "배포할 저장소 선택 · " + Program.DisplayName;
        form.BackColor = Ui.Canvas;
        form.ClientSize = new Size(1080, 680);
        form.MinimumSize = new Size(900, 580);
        form.StartPosition = FormStartPosition.CenterParent;
        var grid = Ui.Grid(("배포", 7), ("GitHub 저장소", 28), ("Win 10/11 파일 패턴", 23), ("Win 7 파일 패턴", 23), ("설명", 35));
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ReadOnly = false;
        grid.Columns.RemoveAt(0);
        grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { HeaderText = "배포", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70 });
        var weights = new[] { 7, 28, 23, 23, 35 };
        for (var index = 0; index < weights.Length; index++) grid.Columns[index].FillWeight = weights[index];
        grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns[1].ReadOnly = grid.Columns[4].ReadOnly = true;
        grid.Columns[1].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        var missing = current.Where(s => !repositories.Any(r => r.FullName.Equals(s.Repository, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new GitHubRepository { FullName = s.Repository, Description = "현재 계정에서 접근 불가 · 체크 해제 시 배포 목록에서 제외됩니다." });
        foreach (var repository in repositories.Concat(missing).OrderBy(r => r.FullName))
        {
            var selected = current.FirstOrDefault(s => s.Repository.Equals(repository.FullName, StringComparison.OrdinalIgnoreCase));
            grid.Rows.Add(selected != null, repository.FullName, selected?.ModernAssetPattern ?? "", selected?.LegacyAssetPattern ?? "", (repository.Private ? "비공개 · " : "") + repository.Description);
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(20) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = Ui.Label(account + " · 클라이언트에 공유할 프로그램에 체크하세요", 14, true); title.Dock = DockStyle.Fill;
        var guide = Ui.Label("정식 Release의 EXE/MSI 설치 파일을 사용합니다. 패턴이 비어 있으면 자동 선택합니다.\n설치 파일이 여러 개면 예: *Setup*.exe / *win7*.exe처럼 구분하세요. 모든 체크를 해제하면 배포 목록이 비워집니다.", 9); guide.Dock = DockStyle.Fill;
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(guide, 0, 1); layout.Controls.Add(grid, 0, 2);
        List<GitHubSelection>? result = null;
        var save = Ui.Button("선택한 저장소 동기화", (_, _) =>
        {
            try
            {
                grid.EndEdit();
                result = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells[0].Value)).Select(r => new GitHubSelection { Repository = Convert.ToString(r.Cells[1].Value)!, ModernAssetPattern = Convert.ToString(r.Cells[2].Value) ?? "", LegacyAssetPattern = Convert.ToString(r.Cells[3].Value) ?? "" }).ToList();
                foreach (var selection in result) selection.Validate();
                form.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(form, ex.Message, Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }, true);
        var cancel = Ui.Button("취소", (_, _) => form.DialogResult = DialogResult.Cancel);
        layout.Controls.Add(Ui.Bar(save, cancel), 0, 3);
        form.AcceptButton = save; form.CancelButton = cancel;
        form.Controls.Add(layout);
        form.ResumeLayout(true);
        return form.ShowDialog(owner) == DialogResult.OK ? result : null;
    }
}
