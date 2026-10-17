using System.Reflection;
using System.Text.Json;

namespace BlockFlow;

public sealed class MainForm : Form, IRunHost
{
    const string FileFilter = "BlockFlow flows (*.exe;*.bflow)|*.exe;*.bflow|Flow programs (*.exe)|*.exe|Flow files (*.bflow)|*.bflow|All files (*.*)|*.*";
    const int MaxUndo = 200;

    Flow flow = new();
    string filePath;
    bool dirty;

    readonly BlockCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly PalettePanel palette = new() { Dock = DockStyle.Fill };
    readonly PropertiesPanel props = new() { Dock = DockStyle.Fill };
    readonly RichTextBox log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(28, 31, 38),
        ForeColor = Color.Gainsboro, Font = Theme.Mono, DetectUrls = false, WordWrap = false,
    };
    readonly SplitContainer outer = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 5 };
    readonly SplitContainer middle = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5 };
    readonly SplitContainer work = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 5 };
    readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel counts = new();
    ToolStripButton runButton, stopButton;
    ToolStripMenuItem runMenu, stopMenu, undoMenu, redoMenu;

    readonly List<string> undo = new(), redo = new();
    Runner runner;

    public MainForm(string openPath)
    {
        Text = "BlockFlow";
        Font = Theme.Ui;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(1280, 820);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }

        BuildLayout();
        BuildMenus();
        WireEvents();

        var settings = Settings.Load();
        if (openPath != null && File.Exists(openPath)) Open(openPath);
        else if (settings.LastFile != null && File.Exists(settings.LastFile)) Open(settings.LastFile);
        else if (!settings.SeenExample) { LoadFlow(BlockLibrary.ExampleFlow(), null); settings.SeenExample = true; settings.Save(); Log("Welcome! This is an example flow — press ▶ Run to try it, or File ▸ New to start fresh.", LogKind.Ok); }
        else LoadFlow(new Flow(), null);
    }

    // ================================================================ layout

    void BuildLayout()
    {
        var paletteTitle = SectionTitle("Blocks");
        outer.Panel1.Controls.Add(palette);
        outer.Panel1.Controls.Add(paletteTitle);
        outer.Panel1.BackColor = Theme.Panel;

        var scriptTitle = SectionTitle("Script — drag to reorder · right-click for more");
        work.Panel1.Controls.Add(canvas);
        work.Panel1.Controls.Add(scriptTitle);

        var propsTitle = SectionTitle("Settings");
        work.Panel2.Controls.Add(props);
        work.Panel2.Controls.Add(propsTitle);
        work.Panel2.BackColor = Theme.Panel;

        var logBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Color.FromArgb(40, 44, 52), ForeColor = Color.White, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 0, 6, 0) };
        logBar.Items.Add(new ToolStripLabel("Run log") { Font = Theme.UiBold, ForeColor = Color.White });
        var clear = new ToolStripButton("Clear") { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.White };
        clear.Click += (_, _) => log.Clear();
        logBar.Items.Add(clear);
        middle.Panel1.Controls.Add(work);
        middle.Panel2.Controls.Add(log);
        middle.Panel2.Controls.Add(logBar);

        outer.Panel2.Controls.Add(middle);
        foreach (var s in new[] { outer, middle, work }) s.BackColor = Theme.Border;
        middle.Panel1.BackColor = Theme.Window;
        Controls.Add(outer);

        var statusStrip = new StatusStrip { BackColor = Theme.Window, SizingGrip = true };
        statusStrip.Items.Add(status);
        statusStrip.Items.Add(counts);
        Controls.Add(statusStrip);
    }

    static Label SectionTitle(string text) => new()
    {
        Text = "  " + text,
        Dock = DockStyle.Top,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = Theme.UiBold,
        ForeColor = Theme.SubText,
        BackColor = Theme.Window,
    };

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        float k = DeviceDpi / 96f;
        outer.SplitterDistance = (int)(230 * k);
        middle.SplitterDistance = Math.Max(200, middle.Height - (int)(170 * k));
        work.SplitterDistance = Math.Max(300, work.Width - (int)(360 * k));
        outer.Panel1MinSize = (int)(170 * k);
        work.Panel2MinSize = (int)(260 * k);
        canvas.Focus();
    }

    void BuildMenus()
    {
        var menu = new MenuStrip { BackColor = Theme.Window, Padding = new Padding(6, 2, 0, 2) };
        ToolStripMenuItem Item(ToolStripMenuItem parent, string text, Keys keys, Action act)
        {
            var it = new ToolStripMenuItem(text, null, (_, _) => act()) { ShortcutKeys = keys };
            parent.DropDownItems.Add(it);
            return it;
        }

        var file = new ToolStripMenuItem("&File");
        Item(file, "&New", Keys.Control | Keys.N, NewFlow);
        Item(file, "&Open…", Keys.Control | Keys.O, OpenDialog);
        Item(file, "&Save (as .exe)", Keys.Control | Keys.S, () => Save());
        Item(file, "Save .exe &As…", Keys.Control | Keys.Shift | Keys.S, () => SaveAs());
        Item(file, "Set the &copy folder…", Keys.None, () => AskCopyFolder(force: true));
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "&Export flow file (.bflow)…", Keys.None, ExportBflow);
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "Load the &example flow", Keys.None, () => { if (ConfirmDiscard()) LoadFlow(BlockLibrary.ExampleFlow(), null); });
        file.DropDownItems.Add(new ToolStripSeparator());
        Item(file, "E&xit", Keys.Alt | Keys.F4, Close);

        var edit = new ToolStripMenuItem("&Edit");
        undoMenu = new ToolStripMenuItem("&Undo", null, (_, _) => Undo()) { ShortcutKeyDisplayString = "Ctrl+Z" };
        redoMenu = new ToolStripMenuItem("&Redo", null, (_, _) => Redo()) { ShortcutKeyDisplayString = "Ctrl+Y" };
        edit.DropDownItems.AddRange(new ToolStripItem[] { undoMenu, redoMenu, new ToolStripSeparator() });
        Item(edit, "&Duplicate block", Keys.Control | Keys.D, canvas.DuplicateSelected);
        edit.DropDownItems.Add(new ToolStripMenuItem("De&lete block", null, (_, _) => canvas.DeleteSelected()) { ShortcutKeyDisplayString = "Del" });
        edit.DropDownItems.Add(new ToolStripMenuItem("Move &up", null, (_, _) => canvas.MoveSelected(-1)) { ShortcutKeyDisplayString = "Ctrl+↑" });
        edit.DropDownItems.Add(new ToolStripMenuItem("Move dow&n", null, (_, _) => canvas.MoveSelected(1)) { ShortcutKeyDisplayString = "Ctrl+↓" });
        Item(edit, "&Turn block on/off", Keys.Control | Keys.E, canvas.ToggleDisabledSelected);

        var run = new ToolStripMenuItem("&Run");
        runMenu = Item(run, "&Run flow", Keys.F5, RunFlow);
        stopMenu = Item(run, "&Stop", Keys.Shift | Keys.F5, StopFlow);
        run.DropDownItems.Add(new ToolStripSeparator());
        Item(run, "Create a &desktop shortcut that runs this flow…", Keys.None, () => CreateShortcut(startup: false));
        Item(run, "Run this flow every time I &log in…", Keys.None, () => CreateShortcut(startup: true));
        Item(run, "Open the &Startup folder", Keys.None, () => System.Diagnostics.Process.Start("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.Startup)));

        var help = new ToolStripMenuItem("&Help");
        Item(help, "&How to use BlockFlow", Keys.F1, ShowHelp);

        menu.Items.AddRange(new ToolStripItem[] { file, edit, run, help });

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, BackColor = Theme.Window, Padding = new Padding(8, 4, 8, 4), ImageScalingSize = new Size(16, 16) };
        ToolStripButton Btn(string text, string tip, Action act)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip, DisplayStyle = ToolStripItemDisplayStyle.Text, Margin = new Padding(2, 0, 2, 0) };
            b.Click += (_, _) => act();
            tools.Items.Add(b);
            return b;
        }
        Btn("📄 New", "New flow (Ctrl+N)", NewFlow);
        Btn("📂 Open", "Open a project — pick a flow .exe you made (or a .bflow file). You can also drag one onto this window. (Ctrl+O)", OpenDialog);
        Btn("💾 Save .exe", "Save this flow as its own program (Ctrl+S). You'll be asked where to put it.", () => Save());
        tools.Items.Add(new ToolStripSeparator());
        Btn("↶ Undo", "Undo (Ctrl+Z)", Undo);
        Btn("↷ Redo", "Redo (Ctrl+Y)", Redo);
        tools.Items.Add(new ToolStripSeparator());
        runButton = Btn("▶  Run", "Run the flow (F5)", RunFlow);
        runButton.Font = Theme.UiBold;
        runButton.ForeColor = Color.FromArgb(0, 140, 70);
        stopButton = Btn("■  Stop", "Stop the running flow (Shift+F5)", StopFlow);
        stopButton.ForeColor = Theme.Error;
        tools.Items.Add(new ToolStripSeparator());
        Btn("🔗 Shortcut", "Make a desktop shortcut that runs this flow with a double-click", () => CreateShortcut(false));
        Btn("❔ Help", "How to use BlockFlow (F1)", ShowHelp);

        Controls.Add(tools);
        Controls.Add(menu);
        MainMenuStrip = menu;

        // right-click menu on blocks
        var ctx = new ContextMenuStrip { Font = Theme.Ui };
        ctx.Items.Add("Duplicate", null, (_, _) => canvas.DuplicateSelected());
        ctx.Items.Add("Turn on/off", null, (_, _) => canvas.ToggleDisabledSelected());
        ctx.Items.Add("Move up", null, (_, _) => canvas.MoveSelected(-1));
        ctx.Items.Add("Move down", null, (_, _) => canvas.MoveSelected(1));
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Delete", null, (_, _) => canvas.DeleteSelected());
        canvas.BlockMenu = ctx;
        UpdateRunState();
    }

    void WireEvents()
    {
        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => OpenDropped(e.Data?.GetData(DataFormats.FileDrop) as string[]);
        canvas.FilesDropped += OpenDropped;
        palette.BlockClicked += type => { canvas.InsertNewAfterSelection(type); canvas.Focus(); };
        canvas.BeforeChange += (_, _) => PushUndo();
        canvas.Changed += (_, _) => MarkDirty();
        canvas.SelectionChanged += (_, _) => props.ShowBlock(canvas.SelectedBlock, flow);
        props.VariableSource = () => flow.DefinedVariables();
        props.BeforeEdit += (_, _) => PushUndo();
        props.Edited += (_, _) => { canvas.Relayout(); MarkDirty(); };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool inText = ActiveControl is TextBoxBase || (ActiveControl is ContainerControl cc && cc.ActiveControl is TextBoxBase)
                      || FindFocused() is TextBoxBase or ComboBox;
        if (!inText && runner == null)
        {
            if (keyData == (Keys.Control | Keys.Z)) { Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { Redo(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    Control FindFocused()
    {
        Control c = this;
        while (c is ContainerControl { ActiveControl: not null } con) c = con.ActiveControl;
        return c;
    }

    // ================================================================ files

    void LoadFlow(Flow f, string path)
    {
        flow = f;
        filePath = path;
        packedFrom = null;
        dirty = false;
        undo.Clear();
        redo.Clear();
        canvas.SetBlocks(flow.Blocks);
        props.ShowBlock(null, flow);
        UpdateTitle();
        UpdateRunState();
        SetStatus(path == null ? "New flow" : "Opened " + path);
    }

    void NewFlow()
    {
        if (!ConfirmDiscard()) return;
        LoadFlow(new Flow(), null);
    }

    void OpenDialog()
    {
        if (!ConfirmDiscard()) return;
        using var d = new OpenFileDialog { Filter = FileFilter, Title = "Open a project (a flow .exe you made, or a .bflow file)" };
        if (filePath != null) d.InitialDirectory = Path.GetDirectoryName(filePath);
        if (d.ShowDialog(this) == DialogResult.OK) Open(d.FileName);
    }

    void Open(string path)
    {
        try
        {
            var loaded = Flow.Load(path);
            LoadFlow(loaded, Path.GetFullPath(path));
            if (IsExe(path)) NotePackedFiles(filePath);
            var s = Settings.Load(); s.LastFile = filePath; s.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that file.\n\n{ex.Message}", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static bool IsExe(string path) => path != null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Saving turns the flow into its own .exe. Asks where the first time; after that, Ctrl+S updates the same .exe.</summary>
    bool Save() => IsExe(filePath) ? WriteExe(filePath) : SaveAs();

    /// <summary>
    /// A flow .exe carries its copy-folder files packed inside it. If the original copy folder isn't on this PC,
    /// the files are used straight from that .exe (never unpacked) and carried over when the project is saved again.
    /// </summary>
    void NotePackedFiles(string exePath)
    {
        if (!string.IsNullOrWhiteSpace(flow.CopyFolder) && Directory.Exists(flow.CopyFolder)) return;
        try
        {
            using var pf = PackedFiles.Open(exePath);
            if (pf == null) return;
            packedFrom = exePath;
            Log($"⇱ This project's {pf.Count} file{(pf.Count == 1 ? "" : "s")} are packed inside {Path.GetFileName(exePath)}. " +
                "They're used straight from there and kept when you save. (Set a copy folder to replace them.)", LogKind.Info);
        }
        catch (Exception ex) { Log($"✖ Couldn't read the files packed in the .exe: {ex.Message}", LogKind.Error); }
    }

    /// <summary>The .exe whose packed files this project uses, when its copy folder isn't on this PC.</summary>
    string packedFrom;

    bool UsePackedFiles => packedFrom != null && File.Exists(packedFrom) &&
                           (string.IsNullOrWhiteSpace(flow.CopyFolder) || !Directory.Exists(flow.CopyFolder));

    void OpenDropped(string[] files)
    {
        var f = files?.FirstOrDefault(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".bflow", StringComparison.OrdinalIgnoreCase));
        if (f == null || runner != null) return;
        if (IsExe(f) && !ExeBuilder.HasProject(f))
        {
            MessageBox.Show(this, $"\"{Path.GetFileName(f)}\" wasn't made by BlockFlow, so there's no project inside it to open.", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (ConfirmDiscard()) Open(f);
    }

    bool SaveAs()
    {
        if (!ExeBuilder.CanBuild)
        {
            MessageBox.Show(this, "Saving as .exe only works from the published BlockFlow.exe.", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        using var d = new SaveFileDialog
        {
            Filter = "Program (*.exe)|*.exe",
            Title = "Where should the .exe be saved?",
            FileName = Runner.SafeName(flow.Name) + ".exe",
            InitialDirectory = filePath != null ? Path.GetDirectoryName(filePath) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            OverwritePrompt = true,
        };
        if (d.ShowDialog(this) != DialogResult.OK) return false;
        if (string.IsNullOrWhiteSpace(flow.CopyFolder) && !AskCopyFolder(force: false)) return false;
        return WriteExe(d.FileName);
    }

    /// <summary>"Do you want a copy folder?" — a folder of files the program uses, packed into the .exe. False = cancelled.</summary>
    bool AskCopyFolder(bool force)
    {
        if (!force)
        {
            var r = MessageBox.Show(this,
                "Do you want a \"copy\" folder?\n\nIt's a folder of files your program uses (like FileCopy.FC). " +
                "Everything in it gets packed inside the .exe, so the .exe always has its files with it.\n\n" +
                "Yes = pick the folder now.\nNo = no copy folder (you can set one later in Flow settings).",
                "Copy folder", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.No) return true;
        }
        using var fb = new FolderBrowserDialog { Description = "Choose the \"copy\" folder — the files your program uses", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (Directory.Exists(flow.CopyFolder)) fb.SelectedPath = flow.CopyFolder;
        if (fb.ShowDialog(this) != DialogResult.OK) return true; // keep going without one
        PushUndo();
        flow.CopyFolder = fb.SelectedPath;
        props.ShowBlock(canvas.SelectedBlock, flow);
        int n = Directory.EnumerateFiles(flow.CopyFolder, "*", SearchOption.AllDirectories).Count();
        Log($"⇱ Copy folder: {flow.CopyFolder} ({n} file{(n == 1 ? "" : "s")})", LogKind.Ok);
        MarkDirty();
        return true;
    }

    bool WriteExe(string path)
    {
        int packed;
        try
        {
            if (!string.IsNullOrWhiteSpace(flow.CopyFolder) && !Directory.Exists(flow.CopyFolder))
                Log($"⚠ The copy folder {flow.CopyFolder} doesn't exist, so no files were packed into the .exe.", LogKind.Warn);
            packed = ExeBuilder.Build(flow, path, UsePackedFiles ? packedFrom : null);
            if (UsePackedFiles) packedFrom = Path.GetFullPath(path); // the new .exe now holds them
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the .exe.\n\n{ex.Message}", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        filePath = Path.GetFullPath(path);
        dirty = false;
        UpdateTitle();
        var s = Settings.Load(); s.LastFile = filePath; s.Save();
        SetStatus("Saved " + filePath);
        Log($"📦 Saved {filePath}" + (packed > 0 ? $" with {packed} file{(packed == 1 ? "" : "s")} from the copy folder packed inside" : "") + " — double-click it to run the flow.", LogKind.Ok);
        return true;
    }

    void ExportBflow()
    {
        using var d = new SaveFileDialog { Filter = "Flow file (*.bflow)|*.bflow", Title = "Export flow file", FileName = Runner.SafeName(flow.Name) + ".bflow", InitialDirectory = FlowFolder };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try { flow.Save(d.FileName); SetStatus("Exported " + d.FileName); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't export.\n\n{ex.Message}", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var r = MessageBox.Show(this, $"Save changes to \"{flow.Name}\"?", "BlockFlow", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r == DialogResult.No || (r == DialogResult.Yes && Save());
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (runner != null)
        {
            if (MessageBox.Show(this, "A flow is still running. Stop it and close?", "BlockFlow", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            { e.Cancel = true; return; }
            runner.StopRequested = true;
        }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    void MarkDirty()
    {
        dirty = true;
        UpdateTitle();
    }

    void UpdateTitle()
    {
        Text = $"{(dirty ? "● " : "")}{flow.Name} — {(filePath != null ? Path.GetFileName(filePath) : "not saved yet")} — BlockFlow";
        int n = flow.Blocks.Count, errs = canvas.Structure.Errors.Count;
        counts.Text = $"{n} block{(n == 1 ? "" : "s")}" + (errs > 0 ? $"   ⚠ {errs} problem{(errs == 1 ? "" : "s")}" : "");
        counts.ForeColor = errs > 0 ? Theme.Error : Theme.SubText;
        undoMenu.Enabled = undo.Count > 0;
        redoMenu.Enabled = redo.Count > 0;
    }

    void SetStatus(string text) => status.Text = text;

    // ================================================================ undo

    void PushUndo()
    {
        undo.Add(Snapshot());
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        redo.Clear();
    }

    string Snapshot() => JsonSerializer.Serialize(new UndoState { Name = flow.Name, Done = flow.ShowDoneMessage, Copy = flow.CopyFolder, Blocks = Flow.BlocksToJson(flow.Blocks), Selected = canvas.SelectedIndex });

    void Restore(string snap)
    {
        var s = JsonSerializer.Deserialize<UndoState>(snap);
        flow.Name = s.Name;
        flow.ShowDoneMessage = s.Done;
        flow.CopyFolder = s.Copy ?? "";
        flow.Blocks = Flow.BlocksFromJson(s.Blocks);
        canvas.SetBlocks(flow.Blocks, s.Selected);
        props.ShowBlock(canvas.SelectedBlock, flow);
        MarkDirty();
    }

    void Undo()
    {
        if (undo.Count == 0 || runner != null) return;
        redo.Add(Snapshot());
        var s = undo[^1]; undo.RemoveAt(undo.Count - 1);
        Restore(s);
        SetStatus("Undone");
    }

    void Redo()
    {
        if (redo.Count == 0 || runner != null) return;
        undo.Add(Snapshot());
        var s = redo[^1]; redo.RemoveAt(redo.Count - 1);
        Restore(s);
        SetStatus("Redone");
    }

    sealed class UndoState
    {
        public string Name { get; set; }
        public bool Done { get; set; }
        public string Copy { get; set; }
        public string Blocks { get; set; }
        public int Selected { get; set; }
    }

    // ================================================================ running

    string FlowFolder => filePath != null ? Path.GetDirectoryName(filePath) : Settings.DefaultFlowFolder();

    void RunFlow()
    {
        if (runner != null) return;
        props.ResetEditGrouping();
        var st = Structure.Analyze(flow.Blocks);
        if (st.Errors.Count > 0)
        {
            canvas.Select(st.Errors[0].Index);
            MessageBox.Show(this, "Please fix these first:\n\n" + string.Join("\n", st.Errors.Select(er => $"• Block {er.Index + 1}: {er.Message}")),
                "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (flow.Blocks.Count == 0) { SetStatus("Add some blocks first."); return; }

        var copy = flow.DeepCopy(); // edits during the run can't affect it
        PackedFiles packed = null;
        if (UsePackedFiles)
        {
            try { packed = PackedFiles.Open(packedFrom); }
            catch (Exception ex) { Log("✖ Couldn't read the files packed in the .exe: " + ex.Message, LogKind.Error); }
        }
        runner = new Runner(copy, this, FlowFolder, packed);
        UpdateRunState();
        if (log.TextLength > 0) AppendLog("", LogKind.Info);
        SetStatus("Running…");
        var r = runner;
        Task.Run(() =>
        {
            RunResult result;
            try { result = r.Run(); }
            catch (Exception ex) { Log("Unexpected error: " + ex.Message, LogKind.Error); result = new RunResult { Outcome = RunOutcome.Crashed, Errors = 1 }; }
            finally { packed?.Dispose(); }
            BeginInvoke(() => RunFinished(result));
        });
    }

    void RunFinished(RunResult result)
    {
        runner = null;
        canvas.SetRunning(-1);
        UpdateRunState();
        SetStatus(result.Outcome switch
        {
            RunOutcome.StoppedByUser => "Stopped.",
            RunOutcome.Invalid => "The flow has problems — see the log.",
            RunOutcome.Crashed => "Something went wrong — see the log.",
            _ => $"Finished. {result.FilesWritten} file(s) written/copied/moved" + (result.Errors > 0 ? $", {result.Errors} problem(s)." : "."),
        });
        if (flow.ShowDoneMessage && result.Outcome is RunOutcome.Finished or RunOutcome.StoppedByBlock)
        {
            var msg = result.Errors > 0
                ? $"Done, but {result.Errors} thing(s) went wrong. Check the red lines in the run log."
                : $"Done! {result.FilesWritten} file(s) written, copied or moved.";
            MessageBox.Show(this, msg, flow.Name, MessageBoxButtons.OK, result.Errors > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
    }

    void StopFlow()
    {
        if (runner == null) return;
        runner.StopRequested = true;
        SetStatus("Stopping…");
    }

    void UpdateRunState()
    {
        bool running = runner != null;
        if (runButton == null) return;
        runButton.Enabled = runMenu.Enabled = !running;
        stopButton.Enabled = stopMenu.Enabled = running;
        canvas.ReadOnly = running;
        palette.Enabled = !running;
        props.Enabled = !running;
    }

    // IRunHost — called from the background run thread
    AskResult IRunHost.Ask(AskRequest request) => (AskResult)Invoke(() => AskDialog.Show(this, request));
    void IRunHost.Log(string message, LogKind kind) => Log(message, kind);
    void IRunHost.Current(int index) => BeginInvoke(() => canvas.SetRunning(index));
    void IRunHost.ShowMessage(string title, string text) =>
        Invoke(() => MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information));

    void Log(string message, LogKind kind)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message, kind)); return; }
        AppendLog(message, kind);
    }

    void AppendLog(string message, LogKind kind)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = Color.FromArgb(110, 118, 130);
        log.AppendText(message.Length == 0 ? "\n" : DateTime.Now.ToString("HH:mm:ss  "));
        if (message.Length == 0) return;
        log.SelectionColor = kind switch
        {
            LogKind.Ok => Color.FromArgb(110, 220, 140),
            LogKind.Warn => Color.FromArgb(240, 190, 90),
            LogKind.Error => Color.FromArgb(255, 110, 110),
            LogKind.Answer => Color.FromArgb(120, 180, 255),
            _ => Color.Gainsboro,
        };
        log.AppendText(message + "\n");
        log.ScrollToCaret();
    }

    // ================================================================ shortcuts & help

    void CreateShortcut(bool startup)
    {
        if (filePath == null || dirty)
        {
            MessageBox.Show(this, "The flow needs to be saved (as an .exe) first, so the shortcut knows what to run.", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!Save()) return;
        }
        string lnk;
        if (startup)
        {
            lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Runner.SafeName(flow.Name) + " (BlockFlow).lnk");
        }
        else
        {
            using var d = new SaveFileDialog
            {
                Filter = "Shortcut (*.lnk)|*.lnk",
                FileName = Runner.SafeName(flow.Name) + ".lnk",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Title = "Where should the shortcut go?",
            };
            if (d.ShowDialog(this) != DialogResult.OK) return;
            lnk = d.FileName;
        }
        try
        {
            if (IsExe(filePath)) WriteShortcut(lnk, filePath, "", Path.GetDirectoryName(filePath), $"Run the BlockFlow flow \"{flow.Name}\"");
            else WriteShortcut(lnk, Environment.ProcessPath, $"\"{filePath}\" --run", Path.GetDirectoryName(filePath), $"Run the BlockFlow flow \"{flow.Name}\"");
            Log($"🔗 Shortcut created: {lnk}", LogKind.Ok);
            MessageBox.Show(this, startup
                    ? $"Done! \"{flow.Name}\" will now run every time you log in to Windows.\n\nTo stop that, use Run ▸ Open the Startup folder and delete the shortcut."
                    : $"Shortcut created:\n{lnk}\n\nDouble-click it to run the flow without opening the editor.",
                "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create the shortcut.\n\n" + ex.Message, "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void WriteShortcut(string lnkPath, string target, string args, string workDir, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        object shell = Activator.CreateInstance(shellType);
        object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
        var t = sc.GetType();
        void SetP(string name, object v) => t.InvokeMember(name, BindingFlags.SetProperty, null, sc, new[] { v });
        SetP("TargetPath", target);
        SetP("Arguments", args);
        SetP("WorkingDirectory", workDir);
        SetP("Description", description);
        SetP("IconLocation", target + ",0");
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    void ShowHelp()
    {
        using var f = new Form
        {
            Text = "How to use BlockFlow",
            Size = new Size(720, 640),
            StartPosition = FormStartPosition.CenterParent,
            Font = Theme.Ui,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            Icon = Icon,
            MinimizeBox = false,
        };
        var box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = new Font("Segoe UI", 10f) };
        box.Text = HelpText;
        f.Controls.Add(box);
        f.ShowDialog(this);
    }

    const string HelpText =
@"BLOCKFLOW — build little programs out of blocks

THE BASICS
• Click a block on the left to add it after the selected block — or drag it into the script exactly where you want it.
• Click a block in the script to change its settings on the right.
• Drag blocks to reorder them. Dragging an If or Repeat moves everything inside it too.
• Press ▶ Run (F5). Ask blocks pop up a box; what you type is saved in a variable.
• Right-click a block to duplicate, turn off, move or delete it. Ctrl+Z undoes anything.

VARIABLES
• Every Ask block saves its answer under a name, e.g. name.
• Write {name} in ANY box (file names, folders, text contents, messages…) and it is replaced with the answer.
• Example file name:  {name}_{date}   →   John_2026-10-17.txt
• Built-ins: {date} {time} {datetime} {year} {month} {day} {loop} {user} {desktop} {documents} {downloads}
• Custom date format: {date:MM-dd-yyyy}.  Change letters: {name:upper}, {name:lower}, {name:title}.

SKIPPING
• Ask boxes have a Skip button (Esc). When you skip, that variable is marked as skipped and becomes empty.
• Copy / Move / Open-folder blocks have ""Don't run this block if an answer it uses was skipped"" turned ON.
  So if you skip the {file} question, the block that copies {file} simply doesn't run.
• Each Ask block can also be set so Skip exits the Repeat, or stops the whole flow.
• Use an If block with ""was skipped"" / ""was answered"" for anything fancier.

FILES
• Write a text file — creates a .txt with your text and variables in it.
• Copy / Move a file — copies the file EXACTLY as it is; nothing is written into it.
  By default it ALWAYS overwrites a file with the same name in the destination folder.
  (You can change that to ""Keep both"" or ""Skip it"".)
• Copy/Move many files — e.g. every *.pdf in Downloads.
• Folders are created automatically when needed.

ASKING AGAIN AND AGAIN
• Put your blocks inside a Repeat (forever). It keeps asking until you press Stop,
  or until you Skip a question whose ""When you press Skip"" is set to ""Exit the current Repeat"".

SAVING = MAKING AN .EXE
• Save (Ctrl+S) asks where to put the .exe and builds it. The .exe IS your flow:
  double-click it and it runs, showing only the question boxes.
• After the first save, Ctrl+S updates that same .exe. ""Save .exe As…"" asks for a new place.
• To change a flow later, open its .exe in BlockFlow (File ▸ Open or drag it in), or run it with --edit.
• File ▸ Export flow file (.bflow) saves a plain text copy if you want one.

THE COPY FOLDER
• The copy folder is just a folder of files your program uses (for example FileCopy.FC).
• When you save the .exe, EVERYTHING in it is packed inside the .exe — so the .exe has its files wherever it goes.
• Nothing is unpacked when the .exe runs: a block copies just the one file it needs straight out of the .exe.
• Add or change files in the copy folder? Save the .exe again so it gets the new versions.
• Use those files with the ""Copy from the copy folder"" block, or type a plain file name (FileCopy.FC)
  in a Copy/Move block. {copyfolder} is the folder's path when the flow runs.
• Set it when you first save, in Flow settings, or with File ▸ Set the copy folder.

OPENING A PROJECT AGAIN
• File ▸ Open (or drag it onto the window) and pick a flow .exe you made — the whole project comes back,
  including the copy-folder files packed inside it.

RUNNING WITHOUT THE EDITOR
• Run ▸ Create a desktop shortcut: double-clicking it runs the flow straight away (just the question boxes).
• Run ▸ Run this flow every time I log in: puts that shortcut in your Startup folder.
• Command line:  BlockFlow.exe ""C:\path\my flow.bflow"" --run

A saved flow .exe needs nothing else installed — you can give it to other people.";
}

/// <summary>Tiny settings file in %AppData%\BlockFlow.</summary>
sealed class Settings
{
    public string LastFile { get; set; }
    public bool SeenExample { get; set; }

    static string Path_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockFlow", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path_)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_));
            File.WriteAllText(Path_, JsonSerializer.Serialize(this));
        }
        catch { /* settings are a convenience only */ }
    }

    public static string DefaultFlowFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BlockFlow");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }
}
