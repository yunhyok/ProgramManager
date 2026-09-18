namespace ProgramManager;

internal static class Ui
{
    public static readonly Color Ink = Color.FromArgb(29, 43, 67);
    public static readonly Color Blue = Color.FromArgb(35, 91, 168);
    public static readonly Color Muted = Color.FromArgb(102, 114, 134);
    public static readonly Color Canvas = Color.FromArgb(245, 247, 250);
    public static readonly Color Border = Color.FromArgb(215, 222, 231);

    public static void BeginForm(Form form)
    {
        // All dimensions below are authored at 96 DPI; defer scaling until all controls exist.
        form.SuspendLayout();
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("맑은 고딕", 10);
        form.BackColor = Color.White;
        form.ForeColor = Ink;
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
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(88, 38), Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 8, 8), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = primary ? Blue : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(27, 76, 145) : Canvas;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(22, 63, 120) : Color.FromArgb(230, 237, 247);
        void Fit() => button.MinimumSize = new Size(button.MinimumSize.Width, ControlHeight(button));
        button.HandleCreated += (_, _) => button.BeginInvoke(new Action(() => { if (!button.IsDisposed) Fit(); }));
        button.FontChanged += (_, _) => { if (button.IsHandleCreated) Fit(); };
        button.DpiChangedAfterParent += (_, _) => Fit();
        button.Click += handler;
        return button;
    }

    private static int ControlHeight(Control control)
    {
        using var graphics = control.CreateGraphics();
        return (int)Math.Ceiling(38 * graphics.DpiY / 96 * control.Font.Size / 10);
    }

    public static ComboBox Choice(int width = 230)
    {
        var choice = new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList, DrawMode = DrawMode.OwnerDrawFixed, BackColor = Color.White, ForeColor = Ink, Margin = new Padding(0, 0, 8, 8), IntegralHeight = false, DropDownHeight = 240 };
        void Fit()
        {
            choice.ItemHeight = Math.Max(1, choice.ItemHeight + ControlHeight(choice) - choice.Height);
            // Native preferred size otherwise reports the smaller, font-only height to layouts.
            choice.MinimumSize = new Size(0, ControlHeight(choice));
            choice.Parent?.PerformLayout();
        }
        // Fields dialogs complete initial DPI scaling in OnLoad, after handles exist.
        choice.HandleCreated += (_, _) => choice.BeginInvoke(new Action(() => { if (!choice.IsDisposed) Fit(); }));
        choice.FontChanged += (_, _) => { if (choice.IsHandleCreated) Fit(); };
        choice.DpiChangedAfterParent += (_, _) => Fit();
        choice.DrawItem += (_, e) =>
        {
            e.DrawBackground();
            var text = e.Index >= 0 ? choice.GetItemText(choice.Items[e.Index]) : choice.Text;
            var inset = Math.Max(4, e.Bounds.Height / 5);
            var bounds = new Rectangle(e.Bounds.X + inset, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 2 * inset), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, e.Font, bounds, choice.Enabled ? e.ForeColor : SystemColors.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            e.DrawFocusRectangle();
        };
        return choice;
    }

    public static Control SearchField(TextBox box)
    {
        var logicalWidth = box.Width;
        var frame = new Panel { Name = "InputField", Width = box.Width, Height = 38, BackColor = Color.White, Margin = new Padding(0, 0, 8, 8), TabStop = false };
        box.BorderStyle = BorderStyle.None;
        frame.Controls.Add(box);
        frame.Layout += (_, _) =>
        {
            var inset = Math.Max(4, frame.Height / 4);
            box.SetBounds(inset, Math.Max(0, (frame.Height - box.PreferredHeight) / 2), Math.Max(1, frame.Width - 2 * inset), box.PreferredHeight);
        };
        void Fit()
        {
            frame.Height = ControlHeight(frame);
            if (frame.Dock == DockStyle.None)
            {
                using var graphics = frame.CreateGraphics();
                frame.Width = (int)Math.Ceiling(logicalWidth * graphics.DpiX / 96 * frame.Font.Size / 10);
            }
        }
        frame.HandleCreated += (_, _) => frame.BeginInvoke(new Action(() => { if (!frame.IsDisposed) Fit(); }));
        frame.FontChanged += (_, _) => { if (frame.IsHandleCreated) Fit(); };
        frame.DpiChangedAfterParent += (_, _) => Fit();
        frame.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, frame.ClientRectangle, box.Focused ? Blue : Border, ButtonBorderStyle.Solid);
        box.GotFocus += (_, _) => frame.Invalidate();
        box.LostFocus += (_, _) => frame.Invalidate();
        frame.Click += (_, _) => box.Focus();
        return frame;
    }

    public static TabControl Tabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(18, 10), DrawMode = TabDrawMode.OwnerDrawFixed };
        tabs.DrawItem += (_, e) =>
        {
            var selected = e.Index == tabs.SelectedIndex;
            var background = SystemInformation.HighContrast ? SystemColors.Window : selected ? Color.White : Canvas;
            var foreground = SystemInformation.HighContrast ? SystemColors.WindowText : selected ? Blue : Muted;
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds, foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected)
            {
                using var accent = new SolidBrush(foreground);
                var height = Math.Max(2, (int)(3 * e.Graphics.DpiY / 96));
                e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Bottom - height, e.Bounds.Width, height);
                if (tabs.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -4, -4));
            }
        };
        return tabs;
    }

    public static Label Label(string text, float size = 10, bool bold = false) => new() { Text = text, AutoSize = true, ForeColor = Ink, Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = new Padding(0, 0, 0, 8) };
    public static FlowLayoutPanel Bar(params Control[] children)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
        foreach (var child in children) child.Anchor = child is Label ? AnchorStyles.Left : AnchorStyles.Top | AnchorStyles.Left;
        bar.Controls.AddRange(children);
        return bar;
    }

    public static DataGridView Grid(params (string Name, int Weight)[] columns)
    {
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Canvas, EnableHeadersVisualStyles = false };
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Canvas, ForeColor = Muted, Padding = new Padding(12, 9, 12, 9), Font = new Font("맑은 고딕", 9, FontStyle.Bold), WrapMode = DataGridViewTriState.False };
        grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, SelectionBackColor = Color.FromArgb(229, 239, 253), SelectionForeColor = Ink, Padding = new Padding(12, 9, 8, 9), WrapMode = DataGridViewTriState.False };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
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
