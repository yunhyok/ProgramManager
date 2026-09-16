using System.Drawing;
using System.Windows.Forms;

// Uses the real production forms. Simulation changes only this process's controls,
// never the user's monitor scale or Windows accessibility settings.
internal sealed class LayoutCheck
{
    private readonly int _percent;
    private readonly List<string> _failures;
    public List<string> Observations { get; } = new();
    public LayoutCheck(int percent, List<string> failures) { _percent = percent; _failures = failures; }

    public void Prepare(Form form)
    {
        using var graphics = form.CreateGraphics();
        var actualDpi = graphics.DpiX; // Framework4.8 DeviceDpi can stay96 with a SystemAware manifest.
        Console.WriteLine($"LAYOUT {(_percent == 0 ? "native" : "simulated-" + _percent)} {form.Text}; actual drawing DPI={actualDpi}; DeviceDpi={form.DeviceDpi}; client={form.ClientSize}");
        if (_percent == 0) { Observations.Add($"native: {form.Text}; actual DPI={actualDpi}; DeviceDpi={form.DeviceDpi}; client={form.ClientSize}"); return; }
        var scale = _percent * 96f / (100 * actualDpi);
        var controls = Descendants(form).ToArray();
        var fonts = controls.Select(control => new Font(control.Font.FontFamily, control.Font.Size * scale, control.Font.Style)).ToArray();
        var grids = controls.OfType<DataGridView>().ToArray();
        var headerFonts = grids.Select(grid => { var font = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font; return new Font(font.FontFamily, font.Size * scale, font.Style); }).ToArray();
        form.SuspendLayout();
        form.Scale(new SizeF(scale, scale));
        for (var index = 0; index < grids.Length; index++) grids[index].ColumnHeadersDefaultCellStyle.Font = headerFonts[index];
        for (var index = 0; index < controls.Length; index++) controls[index].Font = fonts[index];
        form.MinimumSize = Size.Empty;
        // A constrained logical viewport, retaining the production minimum width.
        var logicalViewport = form is ProgramManager.MainForm ? new Size(900, 650) : new Size(560, 450);
        var viewport = new Size(logicalViewport.Width * _percent / 100, logicalViewport.Height * _percent / 100);
        form.ClientSize = viewport;
        form.ResumeLayout(true);
        form.PerformLayout();
        Application.DoEvents();
        Check(form.ClientSize == viewport, "simulated-" + _percent, $"requested viewport {viewport} was clamped to {form.ClientSize}");
        Observations.Add($"simulated-{_percent}: {form.Text}; actual DPI={actualDpi}; DeviceDpi={form.DeviceDpi}; client={form.ClientSize}");
    }

    public void Inspect(Form form, string surface)
    {
        string prefix = (_percent == 0 ? "native" : "simulated-" + _percent) + "/" + surface;
        if (form is ProgramManager.MainForm)
        {
            var controls = Descendants(form).ToArray();
            var search = controls.OfType<TextBox>().Single(c => c.AccessibleName == "프로그램 검색").Parent!;
            var refresh = controls.OfType<Button>().Single(c => c.Text == "목록 새로고침");
            Check(Math.Abs(search.Height - refresh.Height) <= 1 && Math.Abs(search.Top - refresh.Top) <= 1, prefix, $"search/button alignment: {search.Bounds} / {refresh.Bounds}");
            var choice = controls.OfType<ComboBox>().Single(c => c.AccessibleName == "대상 Windows");
            if (choice.Visible)
            {
                var install = controls.OfType<Button>().Single(c => c.Text == "설치 / 업데이트");
                Check(Math.Abs(choice.Height - install.Height) <= 1 && Math.Abs(choice.Top - install.Top) <= 1, prefix, $"Windows dropdown/button alignment: {choice.Bounds} / {install.Bounds}");
                Observations.Add($"{prefix}: Windows dropdown={choice.Bounds}; install={install.Bounds}; search={search.Bounds}; refresh={refresh.Bounds}");
            }
        }
        if (form is ProgramManager.MainForm)
            foreach (var shell in form.Controls.OfType<TableLayoutPanel>())
            {
                Observations.Add($"{prefix}: shell client={shell.ClientSize}; display={shell.DisplayRectangle}; scroll={shell.AutoScrollPosition}; preferred={shell.PreferredSize}");
                Check(!shell.HorizontalScroll.Visible, prefix, "main shell requires horizontal scrolling");
            }
        foreach (var control in Descendants(form).Where(c => c.Visible))
        {
            var name = control.GetType().Name + " " + Short(control.Text);
            if (control is TextBox { Multiline: false } && control.Parent is Panel { Name: "InputField" } frame)
            {
                using var graphics = frame.CreateGraphics();
                var expected = (int)Math.Ceiling(38 * graphics.DpiY / 96 * frame.Font.Size / 10);
                Check(Math.Abs(frame.Height - expected) <= 1, prefix, name + $" input height {frame.Height} differs from shared height {expected}");
                Check(control.Width >= control.Font.Height * 3, prefix, name + " input is too narrow to type into");
            }
            if (control is TableLayoutPanel || control is FlowLayoutPanel)
                Observations.Add($"{prefix}: {name}; bounds={control.Bounds}; display={control.DisplayRectangle}; preferred={control.PreferredSize}; first={Short(control.Controls.Cast<Control>().FirstOrDefault()?.Text ?? "")}");
            if (control is Label label && label.Text.Length > 0 && !label.AutoEllipsis)
            {
                var preferred = label.GetPreferredSize(new Size(label.ClientSize.Width, 0));
                Check(label.ClientSize.Height + 2 >= preferred.Height, prefix, name + $" text height {preferred.Height} > {label.ClientSize.Height}");
                if (form is ProgramManager.MainForm)
                    Check(form.ClientRectangle.Contains(form.RectangleToClient(label.RectangleToScreen(label.ClientRectangle))), prefix, name + " outside main viewport");
            }
            else if (control is ButtonBase button)
            {
                var preferred = button.GetPreferredSize(Size.Empty);
                Check(button.Height + 2 >= preferred.Height && button.Width + 2 >= preferred.Width, prefix, name + $" preferred {preferred} > {button.Size}");
                if (button is Button)
                {
                    using var graphics = button.CreateGraphics();
                    var expectedHeight = (int)Math.Ceiling(38 * graphics.DpiY / 96 * button.Font.Size / 10);
                    Check(Math.Abs(button.Height - expectedHeight) <= 2, prefix, name + $" height {button.Height} differs from shared control height {expectedHeight}");
                }
                if (form is ProgramManager.MainForm)
                    Check(form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))), prefix, name + " outside main viewport");
            }
            if (control.Parent is TableLayoutPanel table && !(table.AutoScroll && (table.VerticalScroll.Visible || table.HorizontalScroll.Visible)))
            {
                var position = table.GetPositionFromControl(control);
                var heights = table.GetRowHeights();
                if (position.Row >= 0 && position.Row < heights.Length)
                {
                    var available = heights.Skip(position.Row).Take(table.GetRowSpan(control)).Sum();
                    Check(control.Height + control.Margin.Vertical <= available + 2, prefix, name + $" occupies {control.Height + control.Margin.Vertical}px in {available}px table row");
                }
            }
            if (control.Parent is FlowLayoutPanel bar && !bar.AutoScroll)
                Check(control.Bottom <= bar.ClientSize.Height - bar.Padding.Bottom + 2 && control.Right <= bar.ClientSize.Width - bar.Padding.Right + 2, prefix, name + $" outside toolbar {bar.ClientSize}, bounds={control.Bounds}");
            if (control is DataGridView grid)
            {
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    var preferred = column.HeaderCell.PreferredSize;
                    var headerStyle = column.HeaderCell.InheritedStyle;
                    using var headerGraphics = grid.CreateGraphics();
                    var measured = TextRenderer.MeasureText(headerGraphics, column.HeaderText, headerStyle.Font, Size.Empty, TextFormatFlags.SingleLine);
                    Observations.Add($"{prefix}: header '{column.HeaderText}' width={column.Width}; minimum={column.MinimumWidth}; preferred={preferred}; content={column.HeaderCell.GetContentBounds(-1)}; font={headerStyle.Font}; padding={headerStyle.Padding}; ownPadding={column.HeaderCell.Style.Padding}; sort={column.SortMode}; auto={column.InheritedAutoSizeMode}; drawDpi={headerGraphics.DpiX}; measured={measured}");
                    Check(grid.ColumnHeadersHeight + 2 >= preferred.Height, prefix, $"header {column.HeaderText}: height {grid.ColumnHeadersHeight} < {preferred.Height}");
                    Check(column.Width + 2 >= preferred.Width, prefix, $"header {column.HeaderText}: width {column.Width} < {preferred.Width}");
                    if (column.HeaderText.Length > 0 && headerStyle.WrapMode != DataGridViewTriState.True)
                        Check(column.HeaderCell.GetContentBounds(-1).Width + 2 >= measured.Width, prefix, $"header {column.HeaderText}: rendered text is narrower than full text ({measured.Width}px)");
                }
                foreach (DataGridViewRow row in grid.Rows)
                {
                    grid.FirstDisplayedScrollingRowIndex = row.Index;
                    Application.DoEvents();
                    var preferred = row.GetPreferredHeight(row.Index, DataGridViewAutoSizeRowMode.AllCellsExceptHeader, true);
                    Check(row.Height + 2 >= preferred, prefix, $"grid row {row.Index}: height {row.Height} < {preferred}");
                }
                if (grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = 0;
                Check(grid.ClientSize.Height >= grid.ColumnHeadersHeight + grid.Font.Height, prefix, "grid has no room for one data row");
            }
        }
        // A dialog may scroll, but its action buttons must still be reachable.
        if (form is not ProgramManager.MainForm)
            foreach (var button in Descendants(form).OfType<Button>().Where(button => button.Visible))
            {
                for (Control? parent = button.Parent; parent != null; parent = parent.Parent)
                {
                    if (parent is ScrollableControl scroll && scroll.AutoScroll) scroll.ScrollControlIntoView(button);
                }
                Application.DoEvents();
                var bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                if (!form.ClientRectangle.Contains(bounds))
                {
                    foreach (var scroll in Descendants(form).OfType<ScrollableControl>().Where(scroll => scroll.AutoScroll && scroll.VerticalScroll.Visible))
                        scroll.AutoScrollPosition = new Point(0, scroll.VerticalScroll.Maximum);
                    Application.DoEvents();
                    bounds = form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                }
                Check(form.ClientRectangle.Contains(bounds), prefix, "dialog button cannot be scrolled into view: " + button.Text + $"; button={bounds}; viewport={form.ClientRectangle}");
            }
        foreach (var scroll in Descendants(form).OfType<ScrollableControl>().Where(scroll => scroll.AutoScroll)) scroll.AutoScrollPosition = Point.Empty;
        Application.DoEvents();
    }

    public void CheckActions(ProgramManager.MainForm form)
    {
        if (_percent != 0) return;
        var controls = Descendants(form).ToArray();
        var buttons = controls.OfType<Button>().ToArray();
        var search = controls.OfType<TextBox>().Single(control => control.AccessibleName == "프로그램 검색");
        var platform = controls.OfType<ComboBox>().Single(control => control.AccessibleName == "대상 Windows");
        search.Text = "no-matching-program-" + Guid.NewGuid().ToString("N");
        foreach (var text in new[] { "실행", "편집", "목록에서 제거", "설치 / 업데이트", "기존 설치 연결", "설명 / 이력" })
            Check(!buttons.Single(button => button.Text == text).Enabled, "actions/empty", text + " should be disabled without a selected program");
        search.Clear();
        foreach (var text in new[] { "실행", "편집", "목록에서 제거", "설치 / 업데이트", "기존 설치 연결" })
            Check(buttons.Single(button => button.Text == text).Enabled, "actions/populated", text + " should be enabled for a compatible selected program");
        var originalPlatform = platform.SelectedIndex;
        platform.SelectedIndex = 1 - originalPlatform;
        foreach (var text in new[] { "설치 / 업데이트", "기존 설치 연결" })
            Check(!buttons.Single(button => button.Text == text).Enabled, "actions/platform", text + " should be disabled for another Windows target");
        platform.SelectedIndex = originalPlatform;
        Observations.Add("Verified empty/filtered list action states and Windows target selection.");
    }

    private void Check(bool condition, string prefix, string message)
    {
        if (!condition) { _failures.Add(prefix + ": " + message); Console.WriteLine("LAYOUT FAIL " + prefix + ": " + message); }
    }
    private static string Short(string text) => text.Replace("\r", " ").Replace("\n", " ").Substring(0, Math.Min(text.Length, 45));
    internal static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (var nested in Descendants(child)) yield return nested;
    }
}
