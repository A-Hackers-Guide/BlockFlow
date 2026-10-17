using System.Drawing.Drawing2D;

namespace BlockFlow;

/// <summary>The block toolbox on the left.</summary>
public sealed class PalettePanel : FlowLayoutPanel
{
    public event Action<string> BlockClicked;
    readonly ToolTip tip = new() { InitialDelay = 400, AutoPopDelay = 15000 };

    public PalettePanel()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        BackColor = Theme.Panel;
        Padding = new Padding(10, 6, 6, 10);

        foreach (var cat in BlockLibrary.Categories)
        {
            Controls.Add(new Label
            {
                Text = cat.ToUpperInvariant(),
                Font = Theme.UiBold,
                ForeColor = Theme.SubText,
                AutoSize = true,
                Margin = new Padding(2, 12, 0, 4),
            });
            foreach (var def in BlockLibrary.All.Where(d => d.Category == cat))
            {
                var item = new PaletteItem(def) { Margin = new Padding(0, 0, 0, 5) };
                item.Click += (_, _) => BlockClicked?.Invoke(def.Type);
                tip.SetToolTip(item, def.Help + "\n\nClick to add after the selected block, or drag it into the script.");
                Controls.Add(item);
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        int w = ClientSize.Width - Padding.Horizontal - 2;
        foreach (Control c in Controls) if (c is PaletteItem) c.Width = Math.Max(100, w);
        base.OnLayout(e);
    }
}

sealed class PaletteItem : Control
{
    readonly BlockDef def;
    bool hot;
    Point down;
    bool pressed;

    public PaletteItem(BlockDef def)
    {
        this.def = def;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Theme.Panel;
        Height = 32;
        Cursor = Cursors.Hand;
        AccessibleName = def.Name;
        AccessibleRole = AccessibleRole.PushButton;
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 2);
        var c = hot ? Theme.Lighter(def.Color, 0.12f) : def.Color;
        using (var path = Theme.Round(r, S(def.IsCloser ? 5 : 8)))
        using (var br = new LinearGradientBrush(r, Theme.Lighter(c, 0.08f), c, LinearGradientMode.Vertical))
        {
            g.FillPath(br, path);
            using var pen = new Pen(Theme.Darker(c, 0.15f));
            g.DrawPath(pen, path);
        }
        var label = def.PairType != null ? def.Name + "  …  " + BlockLibrary.Get(def.PairType).Name : def.Name;
        var iconR = new Rectangle(S(6), 0, S(22), r.Height);
        TextRenderer.DrawText(g, def.Icon, Theme.Icon, iconR, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, label, Theme.UiBold, new Rectangle(S(30), 0, r.Width - S(34), r.Height), Color.White,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        down = e.Location;
        pressed = e.Button == MouseButtons.Left;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressed = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        var ds = SystemInformation.DragSize;
        if (Math.Abs(e.X - down.X) > ds.Width || Math.Abs(e.Y - down.Y) > ds.Height)
        {
            pressed = false;
            var data = new DataObject();
            data.SetData(BlockCanvas.DragFormat, "new:" + def.Type);
            DoDragDrop(data, DragDropEffects.Copy);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        // A drag shouldn't count as a click.
        if (Capture || !ClientRectangle.Contains(PointToClient(MousePosition))) return;
        base.OnClick(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Height = S(32);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Height = S(32);
    }
}
