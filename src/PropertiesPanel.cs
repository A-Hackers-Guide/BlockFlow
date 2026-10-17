namespace BlockFlow;

/// <summary>The right-hand panel that edits the selected block's settings.</summary>
public sealed class PropertiesPanel : Panel
{
    /// <summary>Raised once before the first keystroke of an edit, so it can be undone.</summary>
    public event EventHandler BeforeEdit;
    public event EventHandler Edited;

    public Func<IEnumerable<string>> VariableSource = () => Enumerable.Empty<string>();

    TableLayoutPanel table;
    Block block;
    Flow flow;
    string lastEditKey;
    readonly List<Label> wrapLabels = new();

    public PropertiesPanel()
    {
        AutoScroll = true;
        BackColor = Theme.Panel;
    }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

    public void ShowBlock(Block b, Flow f)
    {
        block = b;
        flow = f;
        lastEditKey = null;
        Rebuild();
    }

    /// <summary>Forget the "already snapshotted" state so the next keystroke creates a new undo step.</summary>
    public void ResetEditGrouping() => lastEditKey = null;

    void Rebuild()
    {
        SuspendLayout();
        var old = table;
        wrapLabels.Clear();
        table = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 12, 14, 20),
            BackColor = Theme.Panel,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (block == null) BuildFlowSettings(); else BuildBlock();
        Controls.Add(table);
        if (old != null) { Controls.Remove(old); old.Dispose(); }
        ResumeLayout(true);
        UpdateWrap();
    }

    void AddRow(Control c, int top = 0, int bottom = 4)
    {
        c.Margin = new Padding(0, top, 0, bottom);
        if (c.Dock == DockStyle.None) c.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(c, 0, table.RowCount++);
    }

    Label Header(string text, Color color)
    {
        var l = new Label
        {
            Text = "  " + text,
            Font = Theme.Header,
            ForeColor = Color.White,
            BackColor = color,
            AutoSize = false,
            Height = S(36),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        return l;
    }

    Label Wrap(string text, Font font, Color color)
    {
        var l = new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, UseMnemonic = false };
        wrapLabels.Add(l);
        return l;
    }

    void UpdateWrap()
    {
        int w = Math.Max(S(120), ClientSize.Width - S(34));
        foreach (var l in wrapLabels) l.MaximumSize = new Size(w, 0);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWrap();
    }

    // ================================================================ flow settings (nothing selected)

    void BuildFlowSettings()
    {
        AddRow(Header("Flow settings", Theme.SubText), 0, 10);
        AddRow(Wrap("Click a block in the script to change its settings here.", Theme.Ui, Theme.SubText), 0, 12);

        AddRow(Wrap("Flow name (shown on the question boxes)", Theme.UiBold, Theme.Text));
        var name = new TextBox { Text = flow.Name, Font = Theme.Ui };
        name.TextChanged += (_, _) => { Edit("flow.name"); flow.Name = name.Text; Edited?.Invoke(this, EventArgs.Empty); };
        AddRow(name, 0, 12);

        var done = new CheckBox { Text = "Show a \"Done\" message when the flow finishes", Checked = flow.ShowDoneMessage, AutoSize = true, Font = Theme.Ui };
        done.CheckedChanged += (_, _) => { Edit("flow.done"); flow.ShowDoneMessage = done.Checked; Edited?.Invoke(this, EventArgs.Empty); };
        AddRow(done, 0, 16);

        AddRow(Wrap("Copy folder", Theme.UiBold, Theme.Text));
        var cf = new TextBox { Text = flow.CopyFolder, Font = Theme.Ui, Dock = DockStyle.Fill, PlaceholderText = "Not set" };
        cf.TextChanged += (_, _) => { Edit("flow.copy"); flow.CopyFolder = cf.Text.Trim(); Edited?.Invoke(this, EventArgs.Empty); };
        AddRow(Wrapped(cf, new List<Button> { BrowseButton(cf, folder: true) }), 0, 2);
        AddRow(Wrap("A folder of files your program uses (e.g. FileCopy.FC). Everything in it is packed inside the .exe when you save. Use the files with \"Copy from the copy folder\", or as {copyfolder}.", Theme.Ui, Theme.SubText), 0, 16);

        AddRow(Wrap("Using variables", Theme.UiBold, Theme.Text), 6);
        AddRow(Wrap("Type a variable name in curly braces in any box, like {name}. It gets replaced by the answer when the flow runs. " +
                    "File names like {name}_{date}.txt become e.g. John_2026-10-17.txt.", Theme.Ui, Theme.SubText), 0, 10);
        AddRow(Wrap("Built-in variables", Theme.UiBold, Theme.Text), 6);
        AddRow(Wrap(string.Join("\n", Runner.BuiltinHelp.Select(h => "{" + h.Name + "}" + (h.Meaning.Length > 0 ? "  — " + h.Meaning : ""))), Theme.Ui, Theme.SubText));
        AddRow(Wrap("Add :upper, :lower or :title to change letters, e.g. {name:upper}.", Theme.Ui, Theme.SubText), 6);
    }

    // ================================================================ block settings

    void BuildBlock()
    {
        var def = block.Def;
        AddRow(Header((def.Icon.Length > 0 ? def.Icon + "  " : "") + def.Name, def.Color), 0, 8);
        AddRow(Wrap(def.Help, Theme.Ui, Theme.SubText), 0, 12);

        if (def.Fields.Count == 0)
            AddRow(Wrap("This block has no settings.", Theme.Ui, Theme.SubText), 4);

        foreach (var f in def.Fields)
        {
            if (f.VisibleIf != null && !f.VisibleIf(block)) continue;

            if (f.Kind == FieldKind.Bool)
            {
                var cb = new CheckBox { Text = f.Label, Checked = block.GetBool(f.Key), AutoSize = true, Font = Theme.Ui };
                cb.CheckedChanged += (_, _) => Commit(f, cb.Checked ? "true" : "false", rebuild: true);
                AddRow(cb, 4, f.Hint.Length > 0 ? 0 : 10);
                if (f.Hint.Length > 0) AddRow(Wrap(f.Hint, Theme.Ui, Theme.SubText), 0, 10);
                continue;
            }

            AddRow(Wrap(f.Label, Theme.UiBold, Theme.Text), 2, 3);
            AddRow(Editor(f), 0, f.Hint.Length > 0 ? 2 : 10);
            if (f.Hint.Length > 0) AddRow(Wrap(f.Hint, Theme.Ui, Theme.SubText), 0, 10);
        }

        var off = new CheckBox { Text = "Turn this block off (it will be skipped)", Checked = block.Disabled, AutoSize = true, Font = Theme.Ui, ForeColor = Theme.SubText };
        off.CheckedChanged += (_, _) => { Edit("disabled"); block.Disabled = off.Checked; Edited?.Invoke(this, EventArgs.Empty); };
        AddRow(off, 16);
    }

    Control Editor(FieldDef f)
    {
        string value = block.Get(f.Key);
        switch (f.Kind)
        {
            case FieldKind.Choice:
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Ui };
                cb.Items.AddRange(f.Choices);
                cb.SelectedItem = f.Choices.Contains(value) ? value : f.Choices[0];
                cb.SelectedIndexChanged += (_, _) => Commit(f, (string)cb.SelectedItem, rebuild: true);
                return cb;
            }
            case FieldKind.Var:
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Font = Theme.Ui, Text = value };
                cb.Items.AddRange(VariableSource().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).Cast<object>().ToArray());
                cb.Text = value;
                cb.TextChanged += (_, _) => Commit(f, cb.Text);
                return cb;
            }
            case FieldKind.Number:
            {
                var n = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Font = Theme.Ui, Width = S(110), Anchor = AnchorStyles.Left };
                n.Value = decimal.TryParse(value, out var d) ? Math.Clamp(d, 0, 1_000_000) : 0;
                if (f.Key == "seconds") n.DecimalPlaces = 1;
                n.ValueChanged += (_, _) => Commit(f, n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                n.Dock = DockStyle.None;
                n.Margin = Padding.Empty;
                return Wrapped(n, null);
            }
            default:
            {
                bool multi = f.Kind == FieldKind.Multi;
                var tb = new TextBox
                {
                    Text = multi ? value.Replace("\r\n", "\n").Replace("\n", "\r\n") : value,
                    Font = multi ? Theme.Mono : Theme.Ui,
                    Multiline = multi,
                    AcceptsReturn = multi,
                    ScrollBars = multi ? ScrollBars.Vertical : ScrollBars.None,
                    Height = multi ? S(120) : S(24),
                    Dock = DockStyle.Fill,
                };
                tb.TextChanged += (_, _) => Commit(f, tb.Text);

                var buttons = new List<Button> { VarButton(tb) };
                if (f.Kind == FieldKind.File) buttons.Add(BrowseButton(tb, folder: false));
                if (f.Kind == FieldKind.Folder) buttons.Add(BrowseButton(tb, folder: true));
                return Wrapped(tb, buttons);
            }
        }
    }

    Control Wrapped(Control main, List<Button> buttons)
    {
        var p = new Panel { Height = main.Height + (main is TextBox { Multiline: true } ? 0 : S(2)), BackColor = Theme.Panel };
        if (main is NumericUpDown) { p.Height = main.Height + S(2); p.Controls.Add(main); main.Location = Point.Empty; return p; }
        p.Controls.Add(main);
        if (buttons != null)
            foreach (var b in buttons)
            {
                b.Dock = DockStyle.Right;
                p.Controls.Add(b);
            }
        main.BringToFront(); // fill takes what the buttons leave
        return p;
    }

    Button SmallButton(string text, string tooltip)
    {
        var b = new Button
        {
            Text = text,
            Width = S(34),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.UiBold,
            BackColor = Theme.Window,
            ForeColor = Theme.Text,
            Cursor = Cursors.Hand,
            TabStop = false,
            UseMnemonic = false,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        new ToolTip().SetToolTip(b, tooltip);
        return b;
    }

    Button VarButton(TextBox tb)
    {
        var b = SmallButton("{ }", "Insert a variable");
        b.Click += (_, _) =>
        {
            var menu = new ContextMenuStrip { Font = Theme.Ui };
            var mine = VariableSource().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            if (mine.Count > 0)
            {
                menu.Items.Add(new ToolStripLabel("Your variables") { Font = Theme.UiBold, ForeColor = Theme.SubText });
                foreach (var v in mine) menu.Items.Add("{" + v + "}", null, (_, _) => Insert(tb, "{" + v + "}"));
                menu.Items.Add(new ToolStripSeparator());
            }
            var built = new ToolStripMenuItem("Built-in");
            foreach (var (name, meaning) in Runner.BuiltinHelp)
                built.DropDownItems.Add("{" + name + "}" + (meaning.Length > 0 ? "   — " + meaning : ""), null, (_, _) => Insert(tb, "{" + name + "}"));
            menu.Items.Add(built);
            menu.Show(b, new Point(0, b.Height));
        };
        return b;
    }

    static void Insert(TextBox tb, string text)
    {
        tb.SelectedText = text;
        tb.Focus();
    }

    Button BrowseButton(TextBox tb, bool folder)
    {
        var b = SmallButton("…", folder ? "Browse for a folder" : "Browse for a file");
        b.Click += (_, _) =>
        {
            if (folder)
            {
                using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Pick a folder", ShowNewFolderButton = true };
                if (Directory.Exists(tb.Text)) d.SelectedPath = tb.Text;
                if (d.ShowDialog(FindForm()) == DialogResult.OK) tb.Text = d.SelectedPath;
            }
            else
            {
                using var d = new OpenFileDialog { Title = "Pick a file", Filter = "All files|*.*" };
                if (File.Exists(tb.Text)) d.FileName = tb.Text;
                if (d.ShowDialog(FindForm()) == DialogResult.OK) tb.Text = d.FileName;
            }
        };
        return b;
    }

    // ================================================================ committing changes

    void Edit(string key)
    {
        if (lastEditKey == key) return; // keep typing in one box as one undo step
        BeforeEdit?.Invoke(this, EventArgs.Empty);
        lastEditKey = key;
    }

    void Commit(FieldDef f, string value, bool rebuild = false)
    {
        if (block == null || block.Get(f.Key) == value) return;
        Edit(f.Key);
        block.Set(f.Key, value);
        Edited?.Invoke(this, EventArgs.Empty);
        // Some fields show/hide others (e.g. "times" only for "A number of times").
        if (rebuild && block.Def.Fields.Any(x => x.VisibleIf != null))
            BeginInvoke(() => { int y = -AutoScrollPosition.Y; Rebuild(); AutoScrollPosition = new Point(0, y); });
    }
}
