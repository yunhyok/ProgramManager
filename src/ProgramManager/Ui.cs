namespace ProgramManager;

internal static class Ui
{
    public static readonly Color Ink = Color.FromArgb(29, 43, 67);
    public static readonly Color Blue = Color.FromArgb(38, 92, 202);
    public static readonly Color Muted = Color.FromArgb(102, 114, 134);
    public static readonly Color Canvas = Color.FromArgb(244, 247, 251);

    public static void BeginForm(Form form)
    {
        // All dimensions below are authored at 96 DPI; defer scaling until all controls exist.
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("맑은 고딕", 10);
        form.Load += (_, _) => FitToScreen(form);
        form.DpiChanged += (_, _) => form.BeginInvoke(new Action(() => FitToScreen(form)));
    }

    private static void FitToScreen(Form form)
    {
        var area = Screen.FromControl(form).WorkingArea;
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, area.Width), Math.Min(form.MinimumSize.Height, area.Height));
        form.Size = new Size(Math.Min(form.Width, area.Width), Math.Min(form.Height, area.Height));
    }

    public static Button Button(string text, EventHandler handler, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(88, 34), Padding = new Padding(10, 3, 10, 3), FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 8, 6), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = primary ? Blue : Color.FromArgb(215, 222, 233);
        button.Click += handler;
        return button;
    }

    public static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, AutoSize = true, ForeColor = Ink, Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 8) };
    public static FlowLayoutPanel Bar(params Control[] children)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 5, 0, 5) };
        bar.Controls.AddRange(children);
        return bar;
    }

    public static DataGridView Grid(params (string Name, int Weight)[] columns)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Canvas, EnableHeadersVisualStyles = false };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(233, 239, 248), ForeColor = Muted, Padding = new Padding(9, 10, 9, 10), Font = new Font("맑은 고딕", 9, FontStyle.Bold), WrapMode = DataGridViewTriState.False };
        grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, SelectionBackColor = Color.FromArgb(224, 235, 255), SelectionForeColor = Ink, Padding = new Padding(9, 10, 5, 10), WrapMode = DataGridViewTriState.False };
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        foreach (var (name, weight) in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.HandleCreated += (_, _) => FitHeaders(grid);
        grid.FontChanged += (_, _) => FitHeaders(grid);
        grid.DpiChangedAfterParent += (_, _) => FitHeaders(grid);
        grid.Layout += (_, _) => FitHeaders(grid);
        return grid;
    }

    public static Icon UpdateIcon(Icon original)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.DrawIcon(original, new Rectangle(0, 0, 32, 32));
            using var badge = new SolidBrush(Color.DarkOrange);
            graphics.FillEllipse(badge, 15, 15, 17, 17);
            using var arrow = new Pen(Color.White, 2);
            graphics.DrawLines(arrow, new Point[] { new(19, 23), new(23, 19), new(27, 23) });
            graphics.DrawLine(arrow, 23, 20, 23, 28);
        }
        var handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    private static void FitHeaders(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
        {
            // PreferredSize omits the first header's extra border pixels.
            var width = column.HeaderCell.PreferredSize.Width + 2;
            if (width > 0 && column.MinimumWidth != width) column.MinimumWidth = width;
        }
    }
}
