namespace BlockFlow;

/// <summary>The pop-up box that asks a question while a flow runs.</summary>
sealed class AskDialog : Form
{
    readonly AskRequest req;
    AskResult result = new() { Outcome = AskOutcome.Stop };
    Control input;
    TextBox pathBox;
    ComboBox choiceBox;

    public static AskResult Show(IWin32Window owner, AskRequest req)
    {
        using var d = new AskDialog(req);
        if (owner == null) { d.TopMost = true; d.ShowInTaskbar = true; d.StartPosition = FormStartPosition.CenterScreen; }
        d.ShowDialog(owner);
        return d.result;
    }

    AskDialog(AskRequest req)
    {
        this.req = req;
        Text = req.Title;
        Font = Theme.Ui;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Panel;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
        };

        var band = new Panel { Height = 6, Width = 476, BackColor = BlockLibrary.AskColor, Margin = Padding.Empty };
        layout.Controls.Add(band);

        var prompt = new Label
        {
            Text = req.Prompt,
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Font = new Font("Segoe UI", 11f),
            ForeColor = Theme.Text,
            Margin = new Padding(18, 16, 18, 10),
            UseMnemonic = false,
        };
        layout.Controls.Add(prompt);

        input = BuildInput();
        if (input != null) layout.Controls.Add(input);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            BackColor = Theme.Window,
            Padding = new Padding(12, 10, 12, 10),
            Margin = new Padding(0, 14, 0, 0),
        };

        Button Btn(string text, Action click, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(88, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? BlockLibrary.AskColor : Color.White,
                ForeColor = primary ? Color.White : Theme.Text,
                Font = primary ? Theme.UiBold : Theme.Ui,
                Margin = new Padding(6, 0, 0, 0),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = primary ? Theme.Darker(BlockLibrary.AskColor, 0.2f) : Theme.Border;
            b.Click += (_, _) => click();
            buttons.Controls.Add(b);
            return b;
        }

        // Right-to-left: first added is right-most.
        if (req.Kind == AskKind.YesNo)
        {
            var yes = Btn("&Yes", () => Finish(AskOutcome.Ok, "Yes"), primary: true);
            Btn("&No", () => Finish(AskOutcome.Ok, "No"));
            AcceptButton = yes;
        }
        else
        {
            var ok = Btn("OK", Ok, primary: true);
            AcceptButton = req.Multi ? null : ok;
        }
        Button skip = req.AllowSkip ? Btn("&Skip", () => Finish(AskOutcome.Skip, "")) : null;
        var stop = Btn("Stop flow", () => Finish(AskOutcome.Stop, ""));
        stop.ForeColor = Theme.Error;
        CancelButton = skip ?? stop;

        var hint = new Label
        {
            Text = req.Multi ? "Ctrl+Enter = OK" : (req.AllowSkip ? "Esc = Skip" : ""),
            AutoSize = true,
            ForeColor = Theme.SubText,
            Margin = new Padding(0, 8, 14, 0),
        };
        buttons.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    Control BuildInput()
    {
        var margin = new Padding(18, 0, 18, 0);
        switch (req.Kind)
        {
            case AskKind.Text:
            {
                var tb = new TextBox
                {
                    Text = req.Default,
                    Width = 440,
                    Multiline = req.Multi,
                    AcceptsReturn = req.Multi,
                    ScrollBars = req.Multi ? ScrollBars.Vertical : ScrollBars.None,
                    Height = req.Multi ? 130 : 28,
                    Font = new Font("Segoe UI", 10.5f),
                    Margin = margin,
                };
                tb.KeyDown += (_, e) => { if (req.Multi && e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Ok(); } };
                return tb;
            }
            case AskKind.Choice:
            {
                choiceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440, Font = new Font("Segoe UI", 10.5f), Margin = margin };
                choiceBox.Items.AddRange(req.Options);
                if (req.Options.Length > 0) choiceBox.SelectedIndex = 0;
                return choiceBox;
            }
            case AskKind.File:
            case AskKind.Folder:
            {
                var row = new TableLayoutPanel { ColumnCount = 2, Width = 440, Height = 32, Margin = margin };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                pathBox = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f), PlaceholderText = req.Kind == AskKind.File ? "Paste a path or click Browse…" : "Paste a folder path or click Browse…" };
                var browse = new Button { Text = "Browse…", AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
                browse.Click += (_, _) => Browse();
                row.Controls.Add(pathBox, 0, 0);
                row.Controls.Add(browse, 1, 0);
                Shown += (_, _) => Browse(); // open the picker straight away
                return row;
            }
        }
        return null;
    }

    void Browse()
    {
        if (req.Kind == AskKind.File)
        {
            using var d = new OpenFileDialog { Title = req.Prompt, Filter = SafeFilter(req.Filter) };
            if (Directory.Exists(req.StartFolder)) d.InitialDirectory = req.StartFolder;
            if (d.ShowDialog(this) == DialogResult.OK) { pathBox.Text = d.FileName; Ok(); }
        }
        else
        {
            using var d = new FolderBrowserDialog { Description = req.Prompt, UseDescriptionForTitle = true, ShowNewFolderButton = true };
            if (d.ShowDialog(this) == DialogResult.OK) { pathBox.Text = d.SelectedPath; Ok(); }
        }
    }

    static string SafeFilter(string f)
    {
        // A filter with an odd number of | parts makes the dialog crash.
        if (string.IsNullOrWhiteSpace(f)) return "All files|*.*";
        return f.Split('|').Length % 2 == 0 ? f : "All files|*.*";
    }

    void Ok()
    {
        switch (req.Kind)
        {
            case AskKind.Text: Finish(AskOutcome.Ok, ((TextBox)input).Text); break;
            case AskKind.Choice: Finish(AskOutcome.Ok, choiceBox.SelectedItem as string ?? ""); break;
            case AskKind.File:
            case AskKind.Folder:
                var p = pathBox.Text.Trim().Trim('"');
                bool exists = req.Kind == AskKind.File ? File.Exists(p) : Directory.Exists(p);
                if (p.Length > 0 && !exists)
                {
                    MessageBox.Show(this, $"That {(req.Kind == AskKind.File ? "file" : "folder")} doesn't exist:\n{p}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (p.Length == 0 && !req.AllowSkip)
                {
                    MessageBox.Show(this, "Please pick one first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Finish(p.Length == 0 ? AskOutcome.Skip : AskOutcome.Ok, p);
                break;
        }
    }

    void Finish(AskOutcome outcome, string value)
    {
        result = new AskResult { Outcome = outcome, Value = value };
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        (input as TextBox)?.SelectAll();
        input?.Focus();
    }
}
