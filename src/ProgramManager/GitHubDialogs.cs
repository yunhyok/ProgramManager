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
        var mode = Ui.Choice();
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

    public static List<GitHubSelection>? Sources(IWin32Window owner, string account, List<GitHubRepository> repositories, List<GitHubSelection> current,
        Func<GitHubRepository, GitHubSelection, CancellationToken, Task<GitHubRepositoryCheck>> check)
    {
        using var form = new Form();
        Ui.BeginForm(form);
        form.Text = "배포할 저장소 선택 · " + Program.DisplayName;
        form.BackColor = Ui.Canvas;
        form.ClientSize = new Size(1080, 680);
        form.MinimumSize = new Size(900, 580);
        form.StartPosition = FormStartPosition.CenterParent;
        var grid = Ui.Grid(("배포", 7), ("GitHub 저장소", 28), ("Win 10/11 파일 패턴", 23), ("Win 7 파일 패턴", 23), ("설명", 25), ("배포 가능 여부", 35));
        grid.Name = "RepositoryGrid";
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ReadOnly = false;
        grid.Columns.RemoveAt(0);
        grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { HeaderText = "배포", AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 70 });
        var weights = new[] { 7, 28, 23, 23, 25, 35 };
        for (var index = 0; index < weights.Length; index++) grid.Columns[index].FillWeight = weights[index];
        grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.Columns[1].ReadOnly = grid.Columns[4].ReadOnly = grid.Columns[5].ReadOnly = true;
        grid.Columns[1].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.Columns[5].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.EditMode = DataGridViewEditMode.EditOnEnter;
        var missing = current.Where(s => !repositories.Any(r => r.FullName.Equals(s.Repository, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new GitHubRepository { FullName = s.Repository, Description = "현재 계정에서 접근 불가 · 체크 해제 시 배포 목록에서 제외됩니다." });
        foreach (var repository in repositories.Concat(missing).OrderBy(r => r.FullName))
        {
            var selected = current.FirstOrDefault(s => s.Repository.Equals(repository.FullName, StringComparison.OrdinalIgnoreCase));
            var row = grid.Rows[grid.Rows.Add(selected != null, repository.FullName, selected?.ModernAssetPattern ?? "", selected?.LegacyAssetPattern ?? "", (repository.Private ? "비공개 · " : "") + repository.Description, "검사 대기")];
            row.Cells[0].ReadOnly = row.Cells[2].ReadOnly = row.Cells[3].ReadOnly = true;
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1, Padding = new Padding(20) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = Ui.Label(account + " · 클라이언트에 공유할 프로그램에 체크하세요", 14, true); title.Dock = DockStyle.Fill;
        var guide = Ui.Label("설치 파일 정보를 검사한 뒤 배포 가능한 저장소를 선택할 수 있습니다. 파일 자체는 받지 않습니다.\n후보가 여러 개면 파일 패턴을 수정하세요 (예: *Setup*.exe / *win7*.exe). 모든 체크를 해제하면 배포 목록이 비워집니다.", 9); guide.Dock = DockStyle.Fill;
        layout.Controls.Add(title, 0, 0); layout.Controls.Add(guide, 0, 1); layout.Controls.Add(grid, 0, 2);
        var details = new TextBox { Name = "RepositoryDetails", AccessibleName = "선택한 저장소 검사 결과", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
        layout.Controls.Add(details, 0, 3);
        var progressText = Ui.Label("설치 파일 검사 준비 중…", 9); progressText.Name = "RepositoryProgressText"; progressText.Dock = DockStyle.Fill;
        var progress = new ProgressBar { Name = "RepositoryProgress", AccessibleName = "저장소 검사 진행률", Dock = DockStyle.Fill, Height = 18, Maximum = Math.Max(1, grid.Rows.Count) };
        var progressPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        progressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); progressPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progressPanel.Controls.Add(progressText, 0, 0); progressPanel.Controls.Add(progress, 0, 1);
        layout.Controls.Add(progressPanel, 0, 4);
        using var cancellation = new CancellationTokenSource();
        var checking = false;
        GitHubSelection Selection(DataGridViewRow row) => new() { Repository = Convert.ToString(row.Cells[1].Value)!, ModernAssetPattern = Convert.ToString(row.Cells[2].Value) ?? "", LegacyAssetPattern = Convert.ToString(row.Cells[3].Value) ?? "" };
        void ShowDetails()
        {
            var row = grid.CurrentRow;
            details.Text = row is null ? "" : Convert.ToString(row.Cells[1].Value) + "\r\n"
                + (row.Tag is GitHubRepositoryCheck { App: null } failed ? failed.Error : Convert.ToString(row.Cells[5].Value));
        }
        void Apply(DataGridViewRow row, GitHubRepositoryCheck result)
        {
            row.Tag = result;
            var available = result.App != null;
            if (!available && !current.Any(s => s.Repository.Equals(result.Repository.FullName, StringComparison.OrdinalIgnoreCase))) row.Cells[0].Value = false;
            row.Cells[0].ReadOnly = !available && !Convert.ToBoolean(row.Cells[0].Value);
            row.Cells[2].ReadOnly = row.Cells[3].ReadOnly = result.Releases is null;
            row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = available ? Ui.Ink : Ui.Muted;
            var reason = result.Error.Replace(result.Repository.FullName, "").TrimStart(' ', ':').Replace("\r", " ").Replace("\n", " ");
            if (reason.Length > 40) reason = reason.Substring(0, 40) + "…";
            row.Cells[5].Value = available
                ? "배포 가능 · " + string.Join(", ", result.App!.Releases.Select(r => r.Platform == Platforms.Legacy ? "Win 7/8" : "Win 10/11").Distinct())
                : (Convert.ToBoolean(row.Cells[0].Value) ? "기존 선택 유지 · " : "선택 불가 · ") + reason;
            row.Cells[5].ToolTipText = result.Error;
            ShowDetails();
        }
        void ShowCompletedCount()
        {
            var available = grid.Rows.Cast<DataGridViewRow>().Count(r => r.Tag is GitHubRepositoryCheck { App: not null });
            progressText.Text = $"검사 완료 {grid.Rows.Count}/{grid.Rows.Count} · 배포 가능 {available}개 · 확인 필요 {grid.Rows.Count - available}개";
        }
        grid.CurrentCellChanged += (_, _) => ShowDetails();
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || checking || !(grid.Rows[e.RowIndex].Tag is GitHubRepositoryCheck result)) return;
            var row = grid.Rows[e.RowIndex];
            if (e.ColumnIndex == 0) Apply(row, result);
            if ((e.ColumnIndex == 2 || e.ColumnIndex == 3) && result.Releases != null)
            {
                var evaluated = CatalogStore.EvaluateGitHubRepository(result.Repository, result.Releases, Selection(row));
                Apply(row, evaluated);
                ShowCompletedCount();
            }
        };
        List<GitHubSelection>? selectedResult = null;
        var save = Ui.Button("선택한 저장소 동기화", (_, _) =>
        {
            try
            {
                grid.EndEdit();
                if (checking) return;
                selectedResult = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToBoolean(r.Cells[0].Value)
                    && (r.Tag is GitHubRepositoryCheck { App: not null } || current.Any(s => s.Repository.Equals(Convert.ToString(r.Cells[1].Value), StringComparison.OrdinalIgnoreCase)))).Select(Selection).ToList();
                foreach (var selection in selectedResult) selection.Validate();
                form.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(form, ex.Message, Program.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }, true);
        save.Enabled = false;
        var cancel = Ui.Button("취소", (_, _) => form.DialogResult = DialogResult.Cancel);
        var retry = Ui.Button("다시 검사", (_, _) => { });
        retry.Click += async (_, _) => await InspectAsync();
        async Task InspectAsync()
        {
            if (checking) return;
            checking = true; save.Enabled = retry.Enabled = false; grid.EndEdit();
            progress.Value = 0;
            foreach (DataGridViewRow row in grid.Rows) row.Cells[0].ReadOnly = row.Cells[2].ReadOnly = row.Cells[3].ReadOnly = true;
            try
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var selection = Selection(row);
                    var info = repositories.FirstOrDefault(r => r.FullName.Equals(selection.Repository, StringComparison.OrdinalIgnoreCase)) ?? new GitHubRepository { FullName = selection.Repository };
                    progressText.Text = $"설치 파일 검사 {progress.Value}/{grid.Rows.Count} · {selection.Repository}";
                    row.Cells[5].Value = "검사 중…";
                    var inspected = await check(info, selection, cancellation.Token);
                    if (form.IsDisposed || cancellation.IsCancellationRequested) return;
                    Apply(row, inspected);
                    // Keep all editing locked until the metadata pass finishes.
                    row.Cells[0].ReadOnly = row.Cells[2].ReadOnly = row.Cells[3].ReadOnly = true;
                    progress.Value++;
                }
                ShowCompletedCount();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!form.IsDisposed) progressText.Text = "검사 중단 · " + ex.Message; }
            finally
            {
                checking = false;
                if (!form.IsDisposed && !cancellation.IsCancellationRequested)
                {
                    foreach (DataGridViewRow row in grid.Rows) if (row.Tag is GitHubRepositoryCheck inspected) Apply(row, inspected);
                    save.Enabled = retry.Enabled = true;
                }
            }
        }
        form.Shown += async (_, _) => await InspectAsync();
        form.FormClosed += (_, _) => cancellation.Cancel();
        layout.Controls.Add(Ui.Bar(save, retry, cancel), 0, 5);
        form.AcceptButton = save; form.CancelButton = cancel;
        form.Controls.Add(layout);
        form.ResumeLayout(true);
        return form.ShowDialog(owner) == DialogResult.OK ? selectedResult : null;
    }

    public static void ShowSyncResult(IWin32Window owner, string summary, GitHubSyncResult result)
    {
        using var form = new Form();
        Ui.BeginForm(form);
        form.Text = "GitHub 동기화 결과 · " + Program.DisplayName;
        form.ClientSize = new Size(780, 480);
        form.MinimumSize = new Size(560, 350);
        form.StartPosition = FormStartPosition.CenterParent;
        var text = summary + "\r\n정상 항목은 반영했습니다. 실패한 기존 항목은 마지막 배포 목록을 유지합니다.\r\n‘저장소 선택’에서 원인을 확인하고 ‘다시 검사’를 누르세요.\r\n\r\n"
            + string.Join("\r\n\r\n", result.Checks.Where(c => c.App is null).Select(c => c.Repository.FullName + "\r\n" + c.Error));
        form.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = text, BackColor = Color.White, AccessibleName = "저장소별 동기화 오류" });
        var close = Ui.Button("닫기", (_, _) => form.Close());
        var footer = Ui.Bar(close); footer.Dock = DockStyle.Bottom;
        form.Controls.Add(footer); form.CancelButton = close;
        form.ResumeLayout(true);
        form.ShowDialog(owner);
    }
}
