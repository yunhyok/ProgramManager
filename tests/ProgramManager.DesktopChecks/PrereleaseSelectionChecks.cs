using ProgramManager;
using ProgramManager.Core;

internal static class PrereleaseSelectionChecks
{
    public static void Run()
    {
        var checks = 0;
        var selected = RunDialog(
            [new GitHubRepository { FullName = "sample-account/preview-tool", Description = "Preview fixture" }],
            [],
            (repository, selection, _) =>
            {
                checks++;
                return Task.FromResult(CatalogStore.EvaluateGitHubRepository(repository,
                [
                    new GitHubRelease { Tag = "v1.0.0", Version = "1.0.0", PublishedUtc = DateTimeOffset.Parse("2026-09-10T00:00:00Z"), Assets = [new GitHubAsset { Id = 1, Name = "Preview-Setup.exe", Size = 1 }] },
                    new GitHubRelease { Tag = "v2.0.0-rc.1", Version = "2.0.0", Prerelease = true, PublishedUtc = DateTimeOffset.Parse("2026-09-11T00:00:00Z"), Assets = [new GitHubAsset { Id = 2, Name = "Preview.zip", Size = 1 }] }
                ], selection));
            },
            dialog =>
            {
                if (!((Button)dialog.AcceptButton!).Enabled) return;
                var grid = Descendants(dialog).OfType<DataGridView>().Single();
                var row = grid.Rows[0];
                Check(grid.Columns.Count == 7 && grid.Columns[6] is DataGridViewCheckBoxColumn && grid.Columns.Cast<DataGridViewColumn>().Take(6).Select(column => column.HeaderText).SequenceEqual(["배포", "GitHub 저장소", "Win 10/11 파일 패턴", "Win 7 파일 패턴", "설명", "배포 가능 여부"]), "preview checkbox is appended after the existing columns");
                Check(!Convert.ToBoolean(row.Cells[6].Value) && !row.Cells[6].ReadOnly, "preview checkbox defaults to false and is editable after cached metadata is available");
                Check(Convert.ToString(row.Cells[5].Value)!.Contains("설치 EXE/MSI") && !Convert.ToString(row.Cells[5].Value)!.Contains("ZIP 다운로드"), "stable installer availability is separate from excluded preview ZIP");
                row.Cells[6].Value = true;
                Check(checks == 1 && Convert.ToString(row.Cells[5].Value)!.Contains("시험판 포함") && Convert.ToString(row.Cells[5].Value)!.Contains("ZIP 다운로드"), "preview toggle reevaluates cached releases without a second API call");
                grid.CurrentCell = row.Cells[1]; row.Selected = true;
                var details = Descendants(dialog).OfType<TextBox>().Single(box => box.Name == "RepositoryDetails");
                Check(details.Text.Contains("v2.0.0-rc.1 · 시험판") && details.Text.Contains("Preview.zip · ZIP 다운로드 (자동 설치/등록 없음)"), "ready details identify preview ZIP downloads without install or registration claims");
                Check(row.Cells[5].ToolTipText.Contains("Preview.zip · 시험판"), "ready tooltip identifies the selected preview file");
                row.Cells[0].Value = true;
                ((Button)dialog.AcceptButton!).PerformClick();
            });
        Check(selected?.Count == 1 && selected[0].IncludePrereleases, "saving retains the preview selection");

        var retained = RunDialog([], [new GitHubSelection { Repository = "sample-account/failed", IncludePrereleases = true }],
            (repository, selection, _) => Task.FromResult(new GitHubRepositoryCheck { Repository = repository, Error = "token rejected" }),
            dialog =>
            {
                if (!((Button)dialog.AcceptButton!).Enabled) return;
                var row = Descendants(dialog).OfType<DataGridView>().Single().Rows[0];
                Check(Convert.ToBoolean(row.Cells[0].Value) && row.Cells[6].ReadOnly && Convert.ToBoolean(row.Cells[6].Value), "failed prior selection stays selected while its preview option remains locked");
                ((Button)dialog.AcceptButton!).PerformClick();
            });
        Check(retained?.Count == 1 && retained[0].IncludePrereleases, "failed prior selection preserves its preview setting");
    }

    private static List<GitHubSelection>? RunDialog(List<GitHubRepository> repositories, List<GitHubSelection> current, Func<GitHubRepository, GitHubSelection, CancellationToken, Task<GitHubRepositoryCheck>> check, Action<Form> inspect)
    {
        using var owner = new Form();
        using var timeout = new System.Windows.Forms.Timer { Interval = 10000 };
        Exception? failure = null;
        EventHandler idle = (_, _) =>
        {
            var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(form => form.Modal);
            if (dialog is null || failure is not null) return;
            try { inspect(dialog); }
            catch (Exception ex) { failure = ex; dialog.DialogResult = DialogResult.Cancel; }
        };
        timeout.Tick += (_, _) =>
        {
            failure = new TimeoutException("Prerelease repository dialog check did not finish.");
            var dialog = Application.OpenForms.Cast<Form>().LastOrDefault(form => form.Modal);
            if (dialog is not null) dialog.DialogResult = DialogResult.Cancel;
        };
        Application.Idle += idle; timeout.Start();
        try
        {
            var result = GitHubDialogs.Sources(owner, "sample-account", repositories, current, check);
            if (failure is not null) throw failure;
            return result;
        }
        finally { timeout.Stop(); Application.Idle -= idle; }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
    }
}
