using System.Drawing.Drawing2D;

namespace BlockFlow;

/// <summary>The script area: draws blocks and handles selecting, dragging and dropping.</summary>
public sealed class BlockCanvas : ScrollableControl
{
    public const string DragFormat = "BlockFlowDrag";

    public List<Block> Blocks { get; private set; } = new();
    public int SelectedIndex { get; private set; } = -1;
    public Block SelectedBlock => SelectedIndex >= 0 && SelectedIndex < Blocks.Count ? Blocks[SelectedIndex] : null;
    public bool ReadOnly { get; set; }
    public ContextMenuStrip BlockMenu { get; set; }

    public event EventHandler SelectionChanged, BeforeChange, Changed;
    public event Action<string[]> FilesDropped;

    Structure st = Structure.Analyze(new List<Block>());
    readonly List<Rectangle> rects = new();
    int running = -1, dropIndex = -1, dragFrom = -1, hover = -1;
    Point downPt;
    readonly ToolTip tip = new() { InitialDelay = 500 };

    public BlockCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        AutoScroll = true;
        AllowDrop = true;
        TabStop = true;
        BackColor = Theme.Canvas;
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    // ================================================================ data

    public void SetBlocks(List<Block> blocks, int select = -1)
    {
        Blocks = blocks;
        running = -1;
        Relayout();
        SelectedIndex = -2; // force the SelectionChanged event
        Select(Math.Min(select, Blocks.Count - 1));
    }

    public void Relayout()
    {
        st = Structure.Analyze(Blocks);
        rects.Clear();
        int y = S(14), width = ClientSize.Width;
        for (int i = 0; i < Blocks.Count; i++)
        {
            int x = S(14) + st.Depth[i] * S(26);
            int h = Blocks[i].Def.Shape switch
            {
                BlockShape.Else => S(30),
                BlockShape.IfEnd or BlockShape.LoopEnd => S(22),
                _ => S(50),
            };
            rects.Add(new Rectangle(x, y, Math.Max(S(240), width - x - S(14)), h));
            y += h + S(6);
        }
        AutoScrollMinSize = new Size(0, y + S(60));
        Invalidate();
    }

    public Structure Structure => st;

    public void Select(int index)
    {
        if (index < -1 || index >= Blocks.Count) index = -1;
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        Invalidate();
        if (index >= 0) EnsureVisible(index);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetRunning(int index)
    {
        running = index;
        if (index >= 0 && index < rects.Count) EnsureVisible(index);
        Invalidate();
    }

    public void EnsureVisible(int i)
    {
        if (i < 0 || i >= rects.Count) return;
        var r = rects[i];
        int top = -AutoScrollPosition.Y, h = ClientSize.Height;
        if (r.Top - S(10) < top) AutoScrollPosition = new Point(0, Math.Max(0, r.Top - S(10)));
        else if (r.Bottom + S(10) > top + h) AutoScrollPosition = new Point(0, r.Bottom + S(10) - h);
    }

    public (int Start, int End) GroupRange(int i)
    {
        if (i < 0 || i >= Blocks.Count) return (i, i);
        return Blocks[i].Def.IsContainer && st.Match[i] >= 0 ? (i, st.Match[i]) : (i, i);
    }

    // ================================================================ editing

    void Before() => BeforeChange?.Invoke(this, EventArgs.Empty);

    void After(int select)
    {
        Relayout();
        SelectedIndex = -2; // force the event
        Select(Math.Clamp(select, -1, Blocks.Count - 1));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void InsertNew(string type, int index)
    {
        if (ReadOnly || BlockLibrary.Get(type) == null) return;
        Before();
        index = Math.Clamp(index, 0, Blocks.Count);
        var b = Block.Create(type);
        Blocks.Insert(index, b);
        if (b.Def.PairType != null) Blocks.Insert(index + 1, Block.Create(b.Def.PairType));
        After(index);
    }

    /// <summary>Clicking a palette block: put it right after the selected block (inside it, if it's an If/Repeat).</summary>
    public void InsertNewAfterSelection(string type) =>
        InsertNew(type, SelectedIndex < 0 ? Blocks.Count : SelectedIndex + 1);

    public void MoveGroup(int from, int to)
    {
        if (ReadOnly) return;
        var (a, z) = GroupRange(from);
        if (to >= a && to <= z + 1) return;
        Before();
        var group = Blocks.GetRange(a, z - a + 1);
        Blocks.RemoveRange(a, group.Count);
        if (to > z) to -= group.Count;
        Blocks.InsertRange(to, group);
        After(to);
    }

    public void DeleteSelected()
    {
        int i = SelectedIndex;
        if (ReadOnly || i < 0) return;
        Before();
        var shape = Blocks[i].Def.Shape;
        if (Blocks[i].Def.IsContainer)
        {
            var (a, z) = GroupRange(i);
            Blocks.RemoveRange(a, z - a + 1);
        }
        else if (Blocks[i].Def.IsCloser && st.Match[i] >= 0)
        {
            // Deleting an End unwraps the If/Repeat but keeps the blocks inside it.
            int start = st.Match[i];
            var remove = new List<int> { i, start };
            if (st.ElseOf[start] >= 0) remove.Add(st.ElseOf[start]);
            foreach (var k in remove.OrderByDescending(k => k)) Blocks.RemoveAt(k);
            i = start;
        }
        else Blocks.RemoveAt(i);
        After(Math.Min(i, Blocks.Count - 1));
    }

    public void DuplicateSelected()
    {
        if (ReadOnly || SelectedIndex < 0) return;
        Before();
        var (a, z) = GroupRange(SelectedIndex);
        var copies = Blocks.GetRange(a, z - a + 1).Select(b => b.Clone()).ToList();
        Blocks.InsertRange(z + 1, copies);
        After(z + 1);
    }

    public void MoveSelected(int dir)
    {
        if (ReadOnly || SelectedIndex < 0) return;
        var (a, z) = GroupRange(SelectedIndex);
        if (dir < 0 && a > 0) MoveGroup(a, a - 1);
        else if (dir > 0 && z < Blocks.Count - 1) MoveGroup(a, z + 2);
    }

    public void ToggleDisabledSelected()
    {
        int i = SelectedIndex;
        if (ReadOnly || i < 0) return;
        Before();
        Blocks[i].Disabled = !Blocks[i].Disabled;
        After(i);
    }

    // ================================================================ painting

    Rectangle R(int i)
    {
        var r = rects[i];
        r.Offset(AutoScrollPosition);
        return r;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawDots(g);

        if (Blocks.Count == 0)
        {
            var box = new Rectangle(S(20), S(40), ClientSize.Width - S(40), S(120));
            using (var pen = new Pen(Theme.CanvasDots, S(2)) { DashStyle = DashStyle.Dash })
            using (var path = Theme.Round(box, S(12)))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "Drag blocks here from the left\n— or click a block to add it —", Theme.Header, box, Theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            DrawDrop(g);
            return;
        }

        // The "arm" that makes If/Repeat look like a C wrapped around its contents.
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (!Blocks[i].Def.IsContainer || st.Match[i] < 0) continue;
            var a = R(i);
            var z = R(st.Match[i]);
            if (z.Bottom < 0 || a.Top > ClientSize.Height) continue;
            var col = BlockColor(i);
            using var br = new SolidBrush(col);
            g.FillRectangle(br, new Rectangle(a.X, a.Bottom - S(8), S(14), z.Top - a.Bottom + S(16)));
            int elseIdx = st.ElseOf[i];
            if (elseIdx >= 0) { var er = R(elseIdx); g.FillRectangle(br, new Rectangle(a.X, er.Top - S(6), S(14), er.Height + S(12))); }
        }

        for (int i = 0; i < Blocks.Count; i++)
        {
            var r = R(i);
            if (r.Bottom < -S(10) || r.Top > ClientSize.Height + S(10)) continue;
            DrawBlock(g, i, r);
        }
        DrawDrop(g);
    }

    void DrawDots(Graphics g)
    {
        int step = S(20), off = AutoScrollPosition.Y % step;
        using var br = new SolidBrush(Theme.CanvasDots);
        for (int y = off; y < ClientSize.Height; y += step)
            for (int x = S(10); x < ClientSize.Width; x += step)
                g.FillRectangle(br, x, y, S(2), S(2));
    }

    Color BlockColor(int i)
    {
        var c = Blocks[i].Def.Color;
        return st.EffectivelyDisabled.Length > i && st.EffectivelyDisabled[i] ? Theme.Blend(c, Theme.Canvas, 0.6f) : c;
    }

    void DrawBlock(Graphics g, int i, Rectangle r)
    {
        var b = Blocks[i];
        var def = b.Def;
        var col = BlockColor(i);
        bool disabled = st.EffectivelyDisabled[i];
        string error = st.ErrorFor(i);
        int radius = def.IsCloser ? S(6) : S(9);

        using (var shadow = Theme.Round(new Rectangle(r.X + S(1), r.Y + S(2), r.Width, r.Height), radius))
        using (var sb = new SolidBrush(Color.FromArgb(disabled ? 15 : 35, 20, 30, 60)))
            g.FillPath(sb, shadow);

        using var path = Theme.Round(r, radius);
        using (var lg = new LinearGradientBrush(r, Theme.Lighter(col, 0.10f), col, LinearGradientMode.Vertical))
            g.FillPath(lg, path);

        if (i == hover && i != SelectedIndex)
            using (var hb = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) g.FillPath(hb, path);

        Color border = error != null ? Theme.Error
                     : i == running ? Theme.Running
                     : i == SelectedIndex ? Theme.Accent
                     : Theme.Darker(col, 0.15f);
        float bw = error != null || i == running || i == SelectedIndex ? S(3) : 1f;
        using (var pen = new Pen(border, bw)) g.DrawPath(pen, path);

        var white = disabled ? Theme.Blend(Color.White, col, 0.25f) : Color.White;
        var soft = Theme.Blend(Color.White, col, disabled ? 0.45f : 0.18f);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        if (def.IsCloser)
        {
            var label = def.Name + (error != null ? "   ⚠ " + error : "");
            TextRenderer.DrawText(g, label, Theme.BlockSummary, new Rectangle(r.X + S(22), r.Y, r.Width - S(30), r.Height), white, flags);
            return;
        }

        // icon bubble
        int bub = S(30);
        var bubble = new Rectangle(r.X + S(9), r.Y + (r.Height - bub) / 2, bub, bub);
        if (def.Shape == BlockShape.Else) { bub = S(20); bubble = new Rectangle(r.X + S(22), r.Y + (r.Height - bub) / 2, bub, bub); }
        using (var bb = new SolidBrush(Color.FromArgb(disabled ? 30 : 55, 255, 255, 255))) g.FillEllipse(bb, bubble);
        TextRenderer.DrawText(g, def.Icon, Theme.Icon, bubble, white,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int tx = bubble.Right + S(10);
        var title = def.Name + (b.Disabled ? "   (turned off)" : "");
        string summary = error != null ? "⚠ " + error : def.Summary(b);

        if (def.Shape == BlockShape.Else)
        {
            TextRenderer.DrawText(g, title, Theme.BlockTitle, new Rectangle(tx, r.Y, r.Right - tx - S(10), r.Height), white, flags);
            return;
        }

        // step number on the right
        var num = (i + 1).ToString();
        var numSize = TextRenderer.MeasureText(num, Theme.BlockSummary);
        TextRenderer.DrawText(g, num, Theme.BlockSummary, new Point(r.Right - numSize.Width - S(8), r.Y + S(6)), soft);

        int textW = r.Right - tx - numSize.Width - S(14);
        TextRenderer.DrawText(g, title, Theme.BlockTitle, new Rectangle(tx, r.Y + S(5), textW, S(20)), white, flags);
        TextRenderer.DrawText(g, summary, Theme.BlockSummary, new Rectangle(tx, r.Y + S(24), textW, S(20)),
            error != null ? Color.FromArgb(255, 235, 140) : soft, flags);
    }

    void DrawDrop(Graphics g)
    {
        if (dropIndex < 0) return;
        int x, y;
        if (Blocks.Count == 0) { x = S(20); y = S(30); }
        else if (dropIndex < Blocks.Count) { var r = R(dropIndex); x = r.X; y = r.Top - S(3); }
        else { var r = R(Blocks.Count - 1); x = S(14) + st.Depth[Blocks.Count - 1] * S(26); y = r.Bottom + S(3); }
        int w = ClientSize.Width - x - S(14);
        using var pen = new Pen(Theme.Accent, S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, x + S(6), y, x + w, y);
        using var br = new SolidBrush(Theme.Accent);
        g.FillEllipse(br, x, y - S(6), S(12), S(12));
    }

    // ================================================================ mouse & keyboard

    int HitTest(Point p)
    {
        for (int i = 0; i < Blocks.Count; i++) if (R(i).Contains(p)) return i;
        return -1;
    }

    int InsertIndexAt(Point p)
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            var r = R(i);
            if (p.Y < r.Top + r.Height / 2) return i;
        }
        return Blocks.Count;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        int hit = HitTest(e.Location);
        Select(hit);
        if (e.Button == MouseButtons.Left) { dragFrom = hit; downPt = e.Location; }
        else if (e.Button == MouseButtons.Right && hit >= 0 && BlockMenu != null) BlockMenu.Show(this, e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left && dragFrom >= 0 && !ReadOnly)
        {
            var ds = SystemInformation.DragSize;
            if (Math.Abs(e.X - downPt.X) > ds.Width || Math.Abs(e.Y - downPt.Y) > ds.Height)
            {
                var data = new DataObject();
                data.SetData(DragFormat, "move:" + dragFrom);
                dragFrom = -1;
                DoDragDrop(data, DragDropEffects.Move);
                dropIndex = -1;
                Invalidate();
            }
            return;
        }
        int h = HitTest(e.Location);
        if (h != hover)
        {
            hover = h;
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            tip.SetToolTip(this, h >= 0 ? (st.ErrorFor(h) ?? Blocks[h].Def.Help) : "");
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != -1) { hover = -1; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragFrom = -1;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        Invalidate();
    }

    static string Payload(DragEventArgs e) =>
        e.Data?.GetDataPresent(DragFormat) == true ? e.Data.GetData(DragFormat) as string : null;

    int DropTarget(DragEventArgs e, string payload)
    {
        int idx = InsertIndexAt(PointToClient(new Point(e.X, e.Y)));
        if (payload.StartsWith("move:"))
        {
            var (a, z) = GroupRange(int.Parse(payload[5..]));
            if (idx >= a && idx <= z + 1) return -1; // dropping onto itself
        }
        return idx;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        var payload = Payload(e);
        if (payload == null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true) { e.Effect = DragDropEffects.Copy; return; }
        if (payload == null || ReadOnly) { e.Effect = DragDropEffects.None; return; }
        e.Effect = payload.StartsWith("move:") ? DragDropEffects.Move : DragDropEffects.Copy;

        var p = PointToClient(new Point(e.X, e.Y));
        int y = -AutoScrollPosition.Y;
        if (p.Y < S(30)) AutoScrollPosition = new Point(0, Math.Max(0, y - S(16)));
        else if (p.Y > ClientSize.Height - S(30)) AutoScrollPosition = new Point(0, y + S(16));

        int idx = DropTarget(e, payload);
        if (idx != dropIndex) { dropIndex = idx; Invalidate(); }
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            FilesDropped?.Invoke(files);
            return;
        }
        var payload = Payload(e);
        int idx = payload == null ? -1 : DropTarget(e, payload);
        dropIndex = -1;
        Invalidate();
        if (idx < 0 || ReadOnly) return;
        if (payload.StartsWith("new:")) InsertNew(payload[4..], idx);
        else if (payload.StartsWith("move:")) MoveGroup(int.Parse(payload[5..]), idx);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        dropIndex = -1;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Delete: DeleteSelected(); break;
            case Keys.Up when e.Control: MoveSelected(-1); break;
            case Keys.Down when e.Control: MoveSelected(1); break;
            case Keys.Up: Select(Math.Max(0, SelectedIndex - 1)); break;
            case Keys.Down: Select(Math.Min(Blocks.Count - 1, SelectedIndex + 1)); break;
            case Keys.Home: Select(Blocks.Count > 0 ? 0 : -1); break;
            case Keys.End: Select(Blocks.Count - 1); break;
            case Keys.Escape: Select(-1); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }
}
