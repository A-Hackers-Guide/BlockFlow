namespace BlockFlow;

/// <summary>Every block type the app knows about.</summary>
public static class BlockLibrary
{
    public static readonly Color AskColor = Color.FromArgb(52, 120, 220);
    public static readonly Color VarColor = Color.FromArgb(226, 112, 24);
    public static readonly Color FileColor = Color.FromArgb(38, 150, 86);
    public static readonly Color LogicColor = Color.FromArgb(132, 84, 226);
    public static readonly Color OutColor = Color.FromArgb(24, 150, 180);
    public static readonly Color NoteColor = Color.FromArgb(118, 124, 134);

    public static readonly string[] Categories = { "Ask", "Variables", "Files", "Logic", "Output" };
    public static readonly List<BlockDef> All = new();
    static readonly Dictionary<string, BlockDef> defs = new(StringComparer.OrdinalIgnoreCase);

    public static BlockDef Get(string type) => defs.TryGetValue(type ?? "", out var d) ? d : null;
    public static bool Exists(string type) => defs.ContainsKey(type ?? "");

    // Choice values (stored in files, so keep them stable)
    public const string SkipMark = "Just mark it as skipped";
    public const string SkipExit = "Exit the current Repeat";
    public const string SkipStop = "Stop the whole flow";

    public const string ExReplace = "Replace it (always overwrite)";
    public const string ExKeepBoth = "Keep both (add a number)";
    public const string ExSkip = "Skip it";
    public const string ExAppend = "Add to the end of it";

    public const string ExEmptyReplace = "Replace it with an empty file";
    public const string ExEmptyLeave = "Leave the existing file alone";

    public const string CopyAll = "Everything in the copy folder";
    public const string CopySome = "Only the file(s) named below";

    public const string NameSyntaxHint =
        "steam_api.dll = that file.   steam_api(64).dll = steam_api.dll AND steam_api64.dll (the part in brackets is optional).   " +
        "*.dll = every .dll.   Several names: separate with ;   If a name isn't there, it's simply skipped.";

    public const string LoopForever = "Forever (until you press Stop)";
    public const string LoopTimes = "A number of times";

    public static readonly string[] IfOps =
    {
        "was skipped", "was answered", "equals", "does not equal", "contains",
        "is empty", "is not empty", "is Yes", "is No",
        "file/folder exists", "file/folder does not exist", "is more than", "is less than",
    };
    public static bool OpNeedsValue(string op) => op is "equals" or "does not equal" or "contains" or "is more than" or "is less than";

    static BlockLibrary() => Build();

    // ---------- field helpers ----------
    static FieldDef Text(string k, string l, string d = "", string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.Text };
    static FieldDef Multi(string k, string l, string d = "", string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.Multi };
    static FieldDef Var(string k, string l, string d, string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.Var };
    static FieldDef Bool(string k, string l, bool d, string h = "") => new() { Key = k, Label = l, Default = d ? "true" : "false", Hint = h, Kind = FieldKind.Bool };
    static FieldDef Choice(string k, string l, string[] c, string h = "") => new() { Key = k, Label = l, Default = c[0], Choices = c, Hint = h, Kind = FieldKind.Choice };
    static FieldDef FileF(string k, string l, string d = "", string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.File };
    static FieldDef Folder(string k, string l, string d = "", string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.Folder };
    static FieldDef Num(string k, string l, string d, string h = "") => new() { Key = k, Label = l, Default = d, Hint = h, Kind = FieldKind.Number };

    static FieldDef SkipField(bool d) => Bool("skipIfSkipped", "Don't run this block if an answer it uses was skipped", d,
        "Example: if you pressed Skip for {file}, a block that uses {file} will not run.");

    static FieldDef[] AskCommon() => new[]
    {
        Bool("allowSkip", "Show a Skip button", true),
        WithVis(Choice("onSkip", "When you press Skip", new[] { SkipMark, SkipExit, SkipStop },
            "\"Mark it as skipped\" keeps going. Blocks that use this answer can then choose not to run."),
            b => b.GetBool("allowSkip")),
    };

    static FieldDef WithVis(FieldDef f, Func<Block, bool> vis) { f.VisibleIf = vis; return f; }

    const string OutputFolder = @"{documents}\BlockFlow Output";
    const string ExistsHint = "\"Replace\" always overwrites the file that's already there.";

    static string Q(string s)
    {
        s = (s ?? "").Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ");
        return "“" + (s.Length > 70 ? s[..67] + "…" : s) + "”";
    }
    static string V(Block b, string key = "var") => "{" + Runner.CleanVarName(b.Get(key)) + "}";
    static string P(string folder, string name) => (folder.Length > 0 ? folder.TrimEnd('\\') + "\\" : "") + name;

    static void Add(BlockDef d) { defs[d.Type] = d; All.Add(d); }

    static void Build()
    {
        // ================= ASK =================
        Add(new BlockDef
        {
            Type = "AskText", Name = "Ask for text", Category = "Ask", Icon = "✎", Color = AskColor,
            Help = "Pops up a box asking you to type something. Your answer is saved in a variable, and you can use it anywhere by writing {its name}.",
            Fields = [
                Var("var", "Save the answer as", "name", "Use it later by typing {name} in any box."),
                Text("prompt", "Question to ask", "What is your name?"),
                Text("default", "Pre-filled answer (optional)"),
                Bool("multi", "Allow several lines", false),
                Bool("emptySkip", "Treat an empty answer as Skip", true),
                .. AskCommon()],
            Summary = b => $"{Q(b.Get("prompt"))}  →  {V(b)}",
        });
        Add(new BlockDef
        {
            Type = "AskChoice", Name = "Ask to pick from a list", Category = "Ask", Icon = "☰", Color = AskColor,
            Help = "Shows a list of options and saves the one you pick.",
            Fields = [
                Var("var", "Save the answer as", "choice"),
                Text("prompt", "Question to ask", "Pick one:"),
                Text("options", "Options (separate with commas)", "Option A, Option B, Option C"),
                .. AskCommon()],
            Summary = b => $"{Q(b.Get("prompt"))}  [{b.Get("options")}]  →  {V(b)}",
        });
        Add(new BlockDef
        {
            Type = "AskYesNo", Name = "Ask Yes or No", Category = "Ask", Icon = "✔", Color = AskColor,
            Help = "Asks a Yes/No question. The variable becomes Yes or No. Pair it with an If block.",
            Fields = [
                Var("var", "Save the answer as", "answer"),
                Text("prompt", "Question to ask", "Do you want to continue?"),
                .. AskCommon()],
            Summary = b => $"{Q(b.Get("prompt"))}  →  {V(b)}",
        });
        Add(new BlockDef
        {
            Type = "AskFile", Name = "Ask to pick a file", Category = "Ask", Icon = "📄", Color = AskColor,
            Help = "Lets you browse for a file. The variable holds the file's full path, ready for a Copy or Move block.",
            Fields = [
                Var("var", "Save the file path as", "file"),
                Text("prompt", "Message to show", "Pick a file:"),
                Folder("start", "Start browsing in (optional)"),
                Text("filter", "File types", "All files|*.*", "Example: Pictures|*.jpg;*.png"),
                .. AskCommon()],
            Summary = b => $"{Q(b.Get("prompt"))}  →  {V(b)}",
        });
        Add(new BlockDef
        {
            Type = "AskFolder", Name = "Ask to pick a folder", Category = "Ask", Icon = "📁", Color = AskColor,
            Help = "Lets you browse for a folder and saves its path.",
            Fields = [
                Var("var", "Save the folder path as", "folder"),
                Text("prompt", "Message to show", "Pick a folder:"),
                .. AskCommon()],
            Summary = b => $"{Q(b.Get("prompt"))}  →  {V(b)}",
        });

        // ================= VARIABLES =================
        Add(new BlockDef
        {
            Type = "SetVar", Name = "Set a variable", Category = "Variables", Icon = "✚", Color = VarColor,
            Help = "Stores some text in a variable. The text can combine other variables, e.g. {first} {last}.",
            Fields = [
                Var("var", "Variable", "fullname"),
                Text("value", "Value", "{name} - {date}")],
            Summary = b => $"{V(b)} = {Q(b.Get("value"))}",
        });
        Add(new BlockDef
        {
            Type = "NextNumber", Name = "Next free number", Category = "Variables", Icon = "#", Color = VarColor,
            Help = "Finds the first number that isn't used yet, so files never clash. Example: Report_{n}.txt gives Report_001.txt, then Report_002.txt...",
            Fields = [
                Var("var", "Save the number as", "n"),
                Folder("folder", "Look in folder", OutputFolder),
                Text("pattern", "File name pattern (must contain {n})", "Report_{n}.txt"),
                Num("start", "Start counting at", "1"),
                Num("digits", "Minimum digits (3 → 001)", "3")],
            Summary = b => $"{V(b)} = first free number for {Q(b.Get("pattern"))}",
        });

        // ================= FILES =================
        Add(new BlockDef
        {
            Type = "WriteText", Name = "Write a text file", Category = "Files", Icon = "💾", Color = FileColor,
            Help = "Creates a text file with whatever you want in it. Both the file name and the contents can use variables like {name} and {date}.",
            Fields = [
                Folder("folder", "Save in folder", OutputFolder, "The folder is created if it doesn't exist."),
                Text("name", "File name", "{name}_{date}", ".txt is added if you don't type an extension."),
                Multi("content", "What to write in the file", "Name: {name}\r\nSaved: {datetime}"),
                Choice("ifExists", "If the file already exists", new[] { ExReplace, ExAppend, ExKeepBoth, ExSkip }),
                SkipField(false)],
            Summary = b => $"{Q(P(b.Get("folder"), b.Get("name")))}  ←  {Q(b.Get("content"))}",
        });
        Add(new BlockDef
        {
            Type = "CopyFile", Name = "Copy a file", Category = "Files", Icon = "⧉", Color = FileColor,
            Help = "Copies a file exactly as it is (its contents are never changed). By default it ALWAYS overwrites a file with the same name in the destination.",
            Fields = [
                FileF("source", "File(s) to copy", "{file}", "Use {file} from an \"Ask to pick a file\" block, browse to a file, or type names of files in the copy folder. " + NameSyntaxHint),
                Folder("dest", "Copy into folder", OutputFolder),
                Text("newName", "New name (optional)", "", "Leave empty to keep the original name. The original extension is kept if you leave it off."),
                Choice("ifExists", "If a file with that name is already there", new[] { ExReplace, ExKeepBoth, ExSkip }, ExistsHint),
                SkipField(true)],
            Summary = b => $"{Q(b.Get("source"))}  →  {Q(P(b.Get("dest"), b.Get("newName")))}",
        });
        Add(new BlockDef
        {
            Type = "MoveFile", Name = "Move a file", Category = "Files", Icon = "➜", Color = FileColor,
            Help = "Moves a file exactly as it is (its contents are never changed). By default it ALWAYS overwrites a file with the same name in the destination.",
            Fields = [
                FileF("source", "File(s) to move", "{file}", "Use {file} from an \"Ask to pick a file\" block, browse to a file, or type names of files in the copy folder. " + NameSyntaxHint),
                Folder("dest", "Move into folder", OutputFolder),
                Text("newName", "New name (optional)", "", "Leave empty to keep the original name. The original extension is kept if you leave it off."),
                Choice("ifExists", "If a file with that name is already there", new[] { ExReplace, ExKeepBoth, ExSkip }, ExistsHint),
                SkipField(true)],
            Summary = b => $"{Q(b.Get("source"))}  →  {Q(P(b.Get("dest"), b.Get("newName")))}",
        });
        Add(new BlockDef
        {
            // (type id kept from older versions so saved projects still load)
            Type = "CopyToCopyFolder", Name = "Copy from the copy folder", Category = "Files", Icon = "⇱", Color = FileColor,
            Help = "Copies your program's own files (from the copy folder) somewhere — always overwriting. By default it copies EVERYTHING in the copy folder. The copy folder is a folder of files your program uses; when you save the .exe, everything in it is packed inside the .exe.",
            Fields = [
                Choice("what", "What to copy", new[] { CopyAll, CopySome }),
                WithVis(Text("source", "File name(s)", "*.*", NameSyntaxHint), b => b.Get("what") == CopySome),
                Folder("dest", "Copy into folder", "", "Leave empty to pick the folder each time the flow runs. Variables like {folder} work too."),
                WithVis(Text("newName", "New name (optional, for a single file)", "", "Leave empty to keep the original name."), b => b.Get("what") == CopySome),
                Choice("ifExists", "If a file with that name is already there", new[] { ExReplace, ExKeepBoth, ExSkip }, ExistsHint),
                SkipField(true)],
            Summary = b => (b.Get("what") == CopySome ? Q(b.Get("source")) + " from the copy folder" : "everything in the copy folder")
                           + "  →  " + (b.Get("dest").Trim().Length == 0 ? "a folder you pick" : Q(P(b.Get("dest"), b.Get("what") == CopySome ? b.Get("newName") : ""))),
        });
        Add(new BlockDef
        {
            Type = "CopyMany", Name = "Copy/Move many files", Category = "Files", Icon = "⇶", Color = FileColor,
            Help = "Copies or moves every file in a folder that matches a pattern, e.g. *.pdf or *.jpg;*.png. Files are never changed, only copied/moved.",
            Fields = [
                Folder("source", "From folder", @"{downloads}"),
                Text("pattern", "Which files", "*.pdf", "Separate several with ; — *.* means everything."),
                Folder("dest", "To folder", OutputFolder),
                Choice("action", "Copy or move?", new[] { "Copy", "Move" }),
                Bool("subfolders", "Include files in subfolders", false),
                Choice("ifExists", "If a file with that name is already there", new[] { ExReplace, ExKeepBoth, ExSkip }, ExistsHint),
                SkipField(true)],
            Summary = b => $"{b.Get("action")} {Q(b.Get("pattern"))} from {Q(b.Get("source"))} → {Q(b.Get("dest"))}",
        });
        Add(new BlockDef
        {
            Type = "EmptyFile", Name = "Create an empty file", Category = "Files", Icon = "🗋", Color = FileColor,
            Help = "Creates a file with nothing inside it.",
            Fields = [
                Folder("folder", "In folder", OutputFolder),
                Text("name", "File name", "{name}.txt"),
                Choice("ifExists", "If the file already exists", new[] { ExEmptyReplace, ExEmptyLeave }),
                SkipField(false)],
            Summary = b => Q(P(b.Get("folder"), b.Get("name"))),
        });
        Add(new BlockDef
        {
            Type = "CreateFolder", Name = "Create a folder", Category = "Files", Icon = "📂", Color = FileColor,
            Help = "Makes a folder (and any missing parent folders). Does nothing if it already exists.",
            Fields = [Folder("path", "Folder", OutputFolder + @"\{name}"), SkipField(false)],
            Summary = b => Q(b.Get("path")),
        });

        // ================= LOGIC =================
        Add(new BlockDef
        {
            Type = "If", Name = "If", Category = "Logic", Icon = "◆", Color = LogicColor, Shape = BlockShape.IfStart, PairType = "EndIf",
            Help = "Runs the blocks inside only when the check is true. Drag an \"Else\" inside to add blocks that run when it's false.",
            Fields = [
                Var("var", "Variable", "name", "Or type text with variables, e.g. {folder}\\{name}.txt for a file check."),
                Choice("op", "Check", IfOps),
                WithVis(Text("value", "Value"), b => OpNeedsValue(b.Get("op")))],
            Summary = b => $"{V(b)} {b.Get("op")}" + (OpNeedsValue(b.Get("op")) ? " " + Q(b.Get("value")) : ""),
        });
        Add(new BlockDef
        {
            Type = "Else", Name = "Else (otherwise)", Category = "Logic", Icon = "◇", Color = LogicColor, Shape = BlockShape.Else,
            Help = "Put this inside an If. Blocks after it run when the If check is false.",
        });
        Add(new BlockDef
        {
            Type = "EndIf", Name = "End If", Category = "Logic", Icon = "", Color = LogicColor, Shape = BlockShape.IfEnd,
            Help = "Marks the end of an If block. (Added automatically with each If.)",
        });
        Add(new BlockDef
        {
            Type = "Repeat", Name = "Repeat", Category = "Logic", Icon = "⟲", Color = LogicColor, Shape = BlockShape.LoopStart, PairType = "EndRepeat",
            Help = "Runs the blocks inside over and over. Great for asking for info again and again. Inside, {loop} is the current round number.",
            Fields = [
                Choice("mode", "How many times", new[] { LoopForever, LoopTimes }),
                WithVis(Num("times", "Times", "3"), b => b.Get("mode") == LoopTimes)],
            Summary = b => b.Get("mode") == LoopTimes ? $"{b.Get("times")} times" : "forever — until you press Stop",
        });
        Add(new BlockDef
        {
            Type = "EndRepeat", Name = "End Repeat", Category = "Logic", Icon = "", Color = LogicColor, Shape = BlockShape.LoopEnd,
            Help = "Marks the end of a Repeat block. (Added automatically with each Repeat.)",
        });
        Add(new BlockDef
        {
            Type = "ExitRepeat", Name = "Exit Repeat", Category = "Logic", Icon = "⏏", Color = LogicColor,
            Help = "Jumps out of the Repeat it's in and continues after its End Repeat.",
            Summary = _ => "leave the Repeat now",
        });
        Add(new BlockDef
        {
            Type = "StopFlow", Name = "Stop the flow", Category = "Logic", Icon = "■", Color = LogicColor,
            Help = "Ends the flow right here.",
            Summary = _ => "end everything here",
        });

        // ================= OUTPUT =================
        Add(new BlockDef
        {
            Type = "Message", Name = "Show a message", Category = "Output", Icon = "💬", Color = OutColor,
            Help = "Pops up a message box. Can include variables.",
            Fields = [Multi("text", "Message", "Saved info for {name}!"), SkipField(false)],
            Summary = b => Q(b.Get("text")),
        });
        Add(new BlockDef
        {
            Type = "OpenFolder", Name = "Open a folder", Category = "Output", Icon = "↗", Color = OutColor,
            Help = "Opens a folder in File Explorer.",
            Fields = [Folder("path", "Folder", OutputFolder), SkipField(true)],
            Summary = b => Q(b.Get("path")),
        });
        Add(new BlockDef
        {
            Type = "Log", Name = "Write to the log", Category = "Output", Icon = "≡", Color = OutColor,
            Help = "Adds a line to the run log at the bottom of the window. Handy for checking variables.",
            Fields = [Text("text", "Text", "name is {name}")],
            Summary = b => Q(b.Get("text")),
        });
        Add(new BlockDef
        {
            Type = "Wait", Name = "Wait", Category = "Output", Icon = "⏱", Color = OutColor,
            Help = "Pauses for a few seconds.",
            Fields = [Num("seconds", "Seconds", "1")],
            Summary = b => $"{b.Get("seconds")} second(s)",
        });
        Add(new BlockDef
        {
            Type = "Comment", Name = "Note", Category = "Output", Icon = "✱", Color = NoteColor,
            Help = "A note to yourself. It does nothing when the flow runs.",
            Fields = [Multi("text", "Note", "Explain what this part does")],
            Summary = b => b.Get("text").Replace("\r\n", " ").Replace("\n", " "),
        });
    }

    /// <summary>A ready-made example that shows the main ideas.</summary>
    public static Flow ExampleFlow()
    {
        Block B(string type, params (string k, string v)[] vals)
        {
            var b = Block.Create(type);
            foreach (var (k, v) in vals) b.Set(k, v);
            return b;
        }
        const string folder = OutputFolder + @"\{name}";
        return new Flow
        {
            Name = "Client Intake",
            ShowDoneMessage = true,
            Blocks =
            [
                B("Comment", ("text", "Keeps asking for client info. Each client gets a folder with an info file. Press Skip on the name question to finish.")),
                B("Repeat"),
                B("AskText", ("var", "name"), ("prompt", "Client name? (Skip to finish)"), ("onSkip", SkipExit)),
                B("AskText", ("var", "phone"), ("prompt", "Phone number?")),
                B("AskText", ("var", "notes"), ("prompt", "Notes?"), ("multi", "true")),
                B("WriteText", ("folder", folder), ("name", "{name}_info"),
                    ("content", "Name: {name}\r\nPhone: {phone}\r\nNotes: {notes}\r\nSaved: {datetime}")),
                B("AskFile", ("var", "file"), ("prompt", "Pick a document to copy into {name}'s folder (or Skip):")),
                B("CopyFile", ("source", "{file}"), ("dest", folder)),
                B("EndRepeat"),
                B("OpenFolder", ("path", OutputFolder)),
            ],
        };
    }
}
