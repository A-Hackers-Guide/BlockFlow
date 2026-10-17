using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockFlow;

public enum LogKind { Info, Ok, Warn, Error, Answer }
public enum AskKind { Text, Choice, YesNo, File, Folder }
public enum AskOutcome { Ok, Skip, Stop }
public enum RunOutcome { Finished, StoppedByUser, StoppedByBlock, Invalid, Crashed }

public sealed class AskRequest
{
    public AskKind Kind;
    public string Title = "", Prompt = "", Default = "", Filter = "", StartFolder = "";
    public string[] Options = Array.Empty<string>();
    public bool Multi, AllowSkip;
}

public sealed class AskResult
{
    public AskOutcome Outcome;
    public string Value = "";
}

public sealed class RunResult
{
    public RunOutcome Outcome = RunOutcome.Finished;
    public int Errors, FilesWritten;
}

/// <summary>Whatever is hosting a run: the editor window, or a shortcut run with no editor.</summary>
public interface IRunHost
{
    AskResult Ask(AskRequest request);
    void Log(string message, LogKind kind);
    void Current(int index);
    void ShowMessage(string title, string text);
}

public sealed class Runner
{
    enum Signal { Next, ExitLoop, StopByUser, StopByBlock }

    sealed class LoopFrame { public int Start, Remaining, Iter = 1; }

    static readonly Regex Token = new(@"\{([A-Za-z_][A-Za-z0-9_ ]*?)(?::([^{}]*))?\}", RegexOptions.Compiled);
    static readonly UTF8Encoding Utf8 = new(false);

    readonly Flow flow;
    readonly IRunHost host;
    readonly string flowFolder;
    readonly Dictionary<string, string> vars = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> skipped = new(StringComparer.OrdinalIgnoreCase);
    readonly Stack<LoopFrame> loops = new();
    readonly RunResult result = new();

    IFileStore store; // the copy folder for this run: a real folder, or the files packed in the .exe
    bool storeChecked;
    public volatile bool StopRequested;

    /// <summary>{copyfolder} when the files are packed inside the .exe (there's no real folder).</summary>
    public const string PackedPrefix = "copy:";

    /// <param name="packed">Files packed inside a flow .exe; read one at a time, only when needed. The caller disposes it.</param>
    public Runner(Flow flow, IRunHost host, string flowFolder, IFileStore packed = null)
    {
        store = packed;
        storeChecked = packed != null;
        this.flow = flow;
        this.host = host;
        this.flowFolder = string.IsNullOrEmpty(flowFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : flowFolder;
    }

    public static string CleanVarName(string s) => (s ?? "").Trim().Trim('{', '}').Trim();

    // ================================================================ run loop

    public RunResult Run()
    {
        var blocks = flow.Blocks;
        var st = Structure.Analyze(blocks);
        if (st.Errors.Count > 0)
        {
            foreach (var (i, msg) in st.Errors) host.Log($"Block {i + 1}: {msg}", LogKind.Error);
            result.Outcome = RunOutcome.Invalid;
            return result;
        }

        host.Log($"▶ Started \"{flow.Name}\"", LogKind.Info);
        int pc = 0;
        while (pc < blocks.Count)
        {
            if (StopRequested) { result.Outcome = RunOutcome.StoppedByUser; break; }

            var b = blocks[pc];
            var def = b.Def;
            host.Current(pc);

            if (b.Disabled)
            {
                pc = def.IsContainer ? st.Match[pc] + 1 : pc + 1;
                continue;
            }

            switch (def.Shape)
            {
                case BlockShape.IfStart:
                    bool ok = EvalIf(b);
                    host.Log($"◆ If {VarLabel(b)} {b.Get("op")}{(BlockLibrary.OpNeedsValue(b.Get("op")) ? " \"" + Expand(b.Get("value")) + "\"" : "")} → {(ok ? "yes" : "no")}", LogKind.Info);
                    pc = ok ? pc + 1 : (st.ElseOf[pc] >= 0 ? st.ElseOf[pc] + 1 : st.Match[pc] + 1);
                    continue;

                case BlockShape.Else: // reached by finishing the "true" part
                    pc = st.Match[pc] + 1;
                    continue;

                case BlockShape.IfEnd:
                    pc++;
                    continue;

                case BlockShape.LoopStart:
                    int times = b.Get("mode") == BlockLibrary.LoopTimes ? ParseInt(Expand(b.Get("times")), 0) : -1;
                    if (times == 0) { pc = st.Match[pc] + 1; continue; }
                    if (times < 0 && !blocks.Skip(pc + 1).Take(st.Match[pc] - pc - 1)
                            .Any(x => !x.Disabled && (x.Type.StartsWith("Ask") || x.Type is "Wait" or "Message")))
                    {
                        host.Log("A \"Repeat forever\" needs an Ask, Wait or Message block inside it, otherwise it would spin endlessly. Stopped to be safe.", LogKind.Error);
                        result.Errors++;
                        result.Outcome = RunOutcome.StoppedByBlock;
                        pc = blocks.Count;
                        continue;
                    }
                    loops.Push(new LoopFrame { Start = pc, Remaining = times });
                    host.Log($"⟲ Repeat — round 1", LogKind.Info);
                    pc++;
                    continue;

                case BlockShape.LoopEnd:
                    var f = loops.Peek();
                    if (f.Remaining < 0 || --f.Remaining > 0)
                    {
                        f.Iter++;
                        host.Log($"⟲ Repeat — round {f.Iter}", LogKind.Info);
                        pc = f.Start + 1;
                    }
                    else
                    {
                        loops.Pop();
                        pc++;
                    }
                    continue;
            }

            Signal sig;
            try
            {
                sig = Exec(b);
            }
            catch (Exception ex)
            {
                result.Errors++;
                host.Log($"✖ {def.Name}: {ex.Message}", LogKind.Error);
                sig = Signal.Next;
            }

            if (sig == Signal.StopByUser) { result.Outcome = RunOutcome.StoppedByUser; break; }
            if (sig == Signal.StopByBlock) { result.Outcome = RunOutcome.StoppedByBlock; break; }
            if (sig == Signal.ExitLoop)
            {
                if (loops.Count == 0) { host.Log("(Not inside a Repeat — continuing.)", LogKind.Warn); pc++; continue; }
                var frame = loops.Pop();
                host.Log("⏏ Left the Repeat", LogKind.Info);
                pc = st.Match[frame.Start] + 1;
                continue;
            }
            pc++;
        }

        host.Current(-1);
        string end = result.Outcome switch
        {
            RunOutcome.StoppedByUser => "■ Stopped.",
            RunOutcome.StoppedByBlock => "■ Flow ended by a Stop block.",
            _ => "✔ Finished.",
        };
        if (result.Errors > 0) end += $"  ({result.Errors} problem{(result.Errors == 1 ? "" : "s")} — see red lines above)";
        host.Log(end, result.Errors > 0 ? LogKind.Warn : LogKind.Ok);
        return result;
    }

    // ================================================================ blocks

    Signal Exec(Block b)
    {
        var def = b.Def;
        if (def.Fields.Any(f => f.Key == "skipIfSkipped") && b.GetBool("skipIfSkipped"))
        {
            var used = UsedSkippedVar(b);
            if (used != null)
            {
                host.Log($"⤼ Skipped \"{def.Name}\" because {{{used}}} was skipped", LogKind.Warn);
                return Signal.Next;
            }
        }

        switch (b.Type)
        {
            case "AskText":
                return DoAsk(b, new AskRequest
                {
                    Kind = AskKind.Text,
                    Default = Expand(b.Get("default")),
                    Multi = b.GetBool("multi"),
                }, emptyIsSkip: b.GetBool("emptySkip"));

            case "AskChoice":
                return DoAsk(b, new AskRequest
                {
                    Kind = AskKind.Choice,
                    Options = Expand(b.Get("options")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                }, emptyIsSkip: true);

            case "AskYesNo":
                return DoAsk(b, new AskRequest { Kind = AskKind.YesNo }, emptyIsSkip: true);

            case "AskFile":
                return DoAsk(b, new AskRequest
                {
                    Kind = AskKind.File,
                    Filter = string.IsNullOrWhiteSpace(b.Get("filter")) ? "All files|*.*" : b.Get("filter"),
                    StartFolder = b.Get("start").Trim().Length > 0 ? ResolvePath(b.Get("start")) : "",
                }, emptyIsSkip: true);

            case "AskFolder":
                return DoAsk(b, new AskRequest { Kind = AskKind.Folder }, emptyIsSkip: true);

            case "SetVar":
            {
                var name = CleanVarName(b.Get("var"));
                if (name.Length == 0) throw new InvalidOperationException("No variable name given.");
                vars[name] = Expand(b.Get("value"));
                skipped.Remove(name);
                host.Log($"✚ {{{name}}} = {Short(vars[name])}", LogKind.Info);
                return Signal.Next;
            }

            case "NextNumber":
            {
                var name = CleanVarName(b.Get("var"));
                var folder = ResolvePath(b.Get("folder"));
                var pattern = b.Get("pattern");
                if (!pattern.Contains("{n}", StringComparison.OrdinalIgnoreCase)) pattern += "_{n}";
                // Expand everything except {n}, then try numbers until one is free.
                var template = Expand(Regex.Replace(pattern, @"\{n\}", "\u0001", RegexOptions.IgnoreCase));
                int start = ParseInt(Expand(b.Get("start")), 1), digits = Math.Clamp(ParseInt(Expand(b.Get("digits")), 1), 1, 12);
                for (int n = start; n < start + 1_000_000; n++)
                {
                    var num = n.ToString().PadLeft(digits, '0');
                    var candidate = Path.Combine(folder, SafeName(template.Replace("\u0001", num)));
                    if (!File.Exists(candidate) && !Directory.Exists(candidate))
                    {
                        vars[name] = num;
                        skipped.Remove(name);
                        host.Log($"# {{{name}}} = {num}", LogKind.Info);
                        return Signal.Next;
                    }
                }
                throw new InvalidOperationException("Couldn't find a free number.");
            }

            case "WriteText":
            {
                var folder = ResolvePath(b.Get("folder"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, WithTxt(SafeName(Expand(b.Get("name")))));
                var content = NormalizeNewlines(Expand(b.Get("content")));
                var policy = b.Get("ifExists");
                bool existed = File.Exists(path);
                if (existed)
                {
                    if (policy == BlockLibrary.ExSkip) { host.Log($"⤼ {Path.GetFileName(path)} already exists — left it alone", LogKind.Warn); return Signal.Next; }
                    if (policy == BlockLibrary.ExAppend)
                    {
                        var old = File.ReadAllText(path);
                        var sep = old.Length == 0 || old.EndsWith('\n') ? "" : "\r\n";
                        ClearReadOnly(path);
                        File.AppendAllText(path, sep + content, Utf8);
                        result.FilesWritten++;
                        host.Log($"💾 Added to {path}", LogKind.Ok);
                        return Signal.Next;
                    }
                    if (policy == BlockLibrary.ExKeepBoth) { path = UniquePath(path); existed = false; }
                    else ClearReadOnly(path);
                }
                File.WriteAllText(path, content, Utf8);
                result.FilesWritten++;
                host.Log($"💾 Wrote {path}{(existed ? "  (replaced old file)" : "")}", LogKind.Ok);
                return Signal.Next;
            }

            case "CopyFile":
            case "MoveFile":
            {
                bool move = b.Type == "MoveFile";
                var raw = Expand(b.Get("source")).Trim().Trim('"');
                if (raw.Length == 0) { host.Log($"⤼ {b.Def.Name}: no file given — nothing to do", LogKind.Warn); return Signal.Next; }
                var dest = ResolvePath(b.Get("dest"));

                if (InStore(raw, out var names))
                {
                    // Files from the copy folder (maybe packed inside the .exe): copy just those files out.
                    if (names.Count == 0) { host.Log($"⤼ \"{raw}\" isn't in the copy folder — skipped", LogKind.Warn); return Signal.Next; }
                    if (move && Store().FolderPath == null) host.Log("(Files inside the program can't be moved out of it — they're copied instead.)", LogKind.Info);
                    Directory.CreateDirectory(dest);
                    foreach (var n in names)
                    {
                        var target = Path.Combine(dest, names.Count == 1 ? TargetName(n, b.Get("newName")) : Path.GetFileName(n));
                        Guard(n, () =>
                        {
                            if (move && Store().FolderPath != null) Transfer(Path.Combine(Store().FolderPath, n), target, true, b.Get("ifExists"));
                            else TransferFromStore(n, target, b.Get("ifExists"));
                        });
                    }
                    return Signal.Next;
                }

                var src = ResolvePath(raw);
                var files = File.Exists(src) ? new List<string> { src } : MatchDisk(src);
                if (files.Count == 0) throw new FileNotFoundException($"Can't find the file \"{src}\".");
                Directory.CreateDirectory(dest);
                foreach (var f in files)
                    Guard(Path.GetFileName(f), () => Transfer(f, Path.Combine(dest, files.Count == 1 ? TargetName(f, b.Get("newName")) : Path.GetFileName(f)), move, b.Get("ifExists")));
                return Signal.Next;
            }

            case "CopyToCopyFolder":
            {
                var fs = Store();
                if (fs == null) { host.Log("⤼ Copy from the copy folder: this flow has no copy folder — skipped", LogKind.Warn); return Signal.Next; }

                bool everything = b.Get("what") != BlockLibrary.CopySome;
                List<string> names;
                if (everything) names = fs.Names.ToList();
                else
                {
                    var spec = Expand(b.Get("source")).Trim().Trim('"');
                    if (spec.StartsWith(PackedPrefix, StringComparison.OrdinalIgnoreCase)) spec = spec[PackedPrefix.Length..];
                    if (fs.FolderPath != null && spec.StartsWith(fs.FolderPath, StringComparison.OrdinalIgnoreCase)) spec = Path.GetRelativePath(fs.FolderPath, spec);
                    names = MatchStore(spec);
                    if (names.Count == 0) { host.Log($"⤼ \"{spec}\" isn't in the copy folder — skipped", LogKind.Warn); return Signal.Next; }
                }
                if (names.Count == 0) { host.Log("⤼ The copy folder is empty — nothing to copy", LogKind.Warn); return Signal.Next; }

                string dest;
                if (b.Get("dest").Trim().Length > 0) dest = ResolvePath(b.Get("dest"));
                else
                {
                    var what = names.Count == 1 ? Path.GetFileName(names[0]) : $"the {names.Count} files from the copy folder";
                    var a = host.Ask(new AskRequest
                    {
                        Kind = AskKind.Folder,
                        Title = flow.Name,
                        Prompt = $"Where should {what} be copied to?",
                        AllowSkip = true,
                    });
                    if (a.Outcome == AskOutcome.Stop) { host.Log("■ You pressed Stop.", LogKind.Warn); return Signal.StopByUser; }
                    if (a.Outcome == AskOutcome.Skip || string.IsNullOrWhiteSpace(a.Value)) { host.Log($"⤼ No folder picked — {what} not copied", LogKind.Warn); return Signal.Next; }
                    dest = Path.GetFullPath(a.Value);
                }
                foreach (var n in names)
                {
                    if (StopRequested) break;
                    // "Everything" keeps any subfolders; a single named file can be renamed.
                    var target = everything ? Path.Combine(dest, n)
                               : Path.Combine(dest, names.Count == 1 ? TargetName(n, b.Get("newName")) : Path.GetFileName(n));
                    Guard(n, () =>
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        TransferFromStore(n, target, b.Get("ifExists"));
                    });
                }
                return Signal.Next;
            }

            case "CopyMany":
            {
                bool move = b.Get("action") == "Move";
                var rawSource = Expand(b.Get("source")).Trim().Trim('"');
                if (rawSource.StartsWith(PackedPrefix, StringComparison.OrdinalIgnoreCase))
                    return CopyManyFromStore(b, rawSource[PackedPrefix.Length..].Trim('\\', '/'));
                var src = ResolvePath(b.Get("source"));
                if (!Directory.Exists(src)) throw new DirectoryNotFoundException($"Can't find the folder \"{src}\".");
                var dest = ResolvePath(b.Get("dest"));
                var opt = b.GetBool("subfolders") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var patterns = Expand(b.Get("pattern")).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (patterns.Length == 0) patterns = new[] { "*.*" };
                var files = patterns.SelectMany(p => Directory.EnumerateFiles(src, p, opt))
                                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // Never pick up files from inside the destination (if it sits inside the source).
                var destFull = Path.GetFullPath(dest).TrimEnd('\\') + "\\";
                files.RemoveAll(f => f.StartsWith(destFull, StringComparison.OrdinalIgnoreCase));
                if (files.Count == 0) { host.Log($"⇶ No files matched {string.Join(";", patterns)} in {src}", LogKind.Warn); return Signal.Next; }
                foreach (var f in files)
                {
                    if (StopRequested) break;
                    var target = opt == SearchOption.AllDirectories
                        ? Path.Combine(dest, Path.GetRelativePath(src, f))
                        : Path.Combine(dest, Path.GetFileName(f));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        Transfer(f, target, move, b.Get("ifExists"));
                    }
                    catch (Exception ex)
                    {
                        result.Errors++;
                        host.Log($"✖ {Path.GetFileName(f)}: {ex.Message}", LogKind.Error);
                    }
                }
                return Signal.Next;
            }

            case "EmptyFile":
            {
                var folder = ResolvePath(b.Get("folder"));
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, SafeName(Expand(b.Get("name"))));
                if (File.Exists(path) && b.Get("ifExists") == BlockLibrary.ExEmptyLeave)
                {
                    host.Log($"⤼ {Path.GetFileName(path)} already exists — left it alone", LogKind.Warn);
                    return Signal.Next;
                }
                ClearReadOnly(path);
                File.WriteAllBytes(path, Array.Empty<byte>());
                result.FilesWritten++;
                host.Log($"🗋 Created empty file {path}", LogKind.Ok);
                return Signal.Next;
            }

            case "CreateFolder":
            {
                var path = ResolvePath(b.Get("path"));
                Directory.CreateDirectory(path);
                host.Log($"📂 Folder ready: {path}", LogKind.Ok);
                return Signal.Next;
            }

            case "OpenFolder":
            {
                var path = ResolvePath(b.Get("path"));
                if (!Directory.Exists(path)) { host.Log($"↗ Folder doesn't exist yet: {path}", LogKind.Warn); return Signal.Next; }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                host.Log($"↗ Opened {path}", LogKind.Info);
                return Signal.Next;
            }

            case "Message":
                host.ShowMessage(flow.Name, NormalizeNewlines(Expand(b.Get("text"))));
                return Signal.Next;

            case "Log":
                host.Log("≡ " + Expand(b.Get("text")), LogKind.Info);
                return Signal.Next;

            case "Wait":
            {
                double secs = double.TryParse(Expand(b.Get("seconds")), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 1;
                var until = DateTime.UtcNow.AddSeconds(Math.Clamp(secs, 0, 86400));
                while (DateTime.UtcNow < until && !StopRequested) Thread.Sleep(50);
                return Signal.Next;
            }

            case "ExitRepeat": return Signal.ExitLoop;
            case "StopFlow": return Signal.StopByBlock;
            case "Comment": return Signal.Next;
        }
        throw new InvalidOperationException($"Unknown block type \"{b.Type}\".");
    }

    Signal DoAsk(Block b, AskRequest req, bool emptyIsSkip)
    {
        var name = CleanVarName(b.Get("var"));
        if (name.Length == 0) throw new InvalidOperationException("No variable name given to save the answer in.");

        req.Prompt = NormalizeNewlines(Expand(b.Get("prompt")));
        req.AllowSkip = b.GetBool("allowSkip");
        req.Title = flow.Name + (loops.Count > 0 ? $"  —  #{loops.Peek().Iter}" : "");

        var answer = host.Ask(req);
        if (answer.Outcome == AskOutcome.Stop) { host.Log("■ You pressed Stop.", LogKind.Warn); return Signal.StopByUser; }

        bool wasSkipped = answer.Outcome == AskOutcome.Skip || (emptyIsSkip && string.IsNullOrWhiteSpace(answer.Value));
        if (!wasSkipped)
        {
            vars[name] = answer.Value ?? "";
            skipped.Remove(name);
            host.Log($"✎ {{{name}}} = {Short(vars[name])}", LogKind.Answer);
            return Signal.Next;
        }

        vars[name] = "";
        skipped.Add(name);
        host.Log($"⤼ {{{name}}} skipped", LogKind.Warn);
        return b.Get("onSkip") switch
        {
            BlockLibrary.SkipExit => Signal.ExitLoop,
            BlockLibrary.SkipStop => Signal.StopByBlock,
            _ => Signal.Next,
        };
    }

    bool EvalIf(Block b)
    {
        var left = b.Get("var").Trim();
        bool isPlainVar = !left.Contains('{') || Token.Match(left) is { Success: true } m && m.Value == left;
        var name = CleanVarName(left);
        string val = isPlainVar ? Lookup(name, null) ?? "" : Expand(left);
        var right = Expand(b.Get("value")).Trim();
        // File checks: text that isn't a variable (e.g. steam_api(64).dll) is the file name itself.
        if (isPlainVar && !left.Contains('{') && Lookup(name, null) == null && b.Get("op").StartsWith("file/folder"))
            val = left;

        switch (b.Get("op"))
        {
            case "was skipped": return skipped.Contains(name);
            case "was answered": return vars.ContainsKey(name) && !skipped.Contains(name);
            case "equals": return string.Equals(val.Trim(), right, StringComparison.OrdinalIgnoreCase);
            case "does not equal": return !string.Equals(val.Trim(), right, StringComparison.OrdinalIgnoreCase);
            case "contains": return val.Contains(right, StringComparison.OrdinalIgnoreCase);
            case "is empty": return string.IsNullOrWhiteSpace(val);
            case "is not empty": return !string.IsNullOrWhiteSpace(val);
            case "is Yes": return val.Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase);
            case "is No": return val.Trim().Equals("No", StringComparison.OrdinalIgnoreCase);
            case "file/folder exists": return val.Trim().Length > 0 && ExistsAnywhere(val.Trim());
            case "file/folder does not exist": return val.Trim().Length == 0 || !ExistsAnywhere(val.Trim());
            case "is more than": return Num(val) is double a && Num(right) is double c && a > c;
            case "is less than": return Num(val) is double a2 && Num(right) is double c2 && a2 < c2;
        }
        return false;
    }

    static double? Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var d) ||
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;

    static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>On disk, in the copy folder, or matching a name pattern like steam_api(64).dll.</summary>
    bool ExistsAnywhere(string v)
    {
        if (InStore(v, out var names)) return names.Count > 0;
        var full = ResolveRaw(v);
        return PathExists(full) || MatchDisk(full).Count > 0;
    }

    string VarLabel(Block b) => b.Get("var").Contains('{') ? b.Get("var") : "{" + CleanVarName(b.Get("var")) + "}";

    // ================================================================ variables

    string UsedSkippedVar(Block b)
    {
        foreach (var (k, v) in b.Values)
        {
            if (k == "skipIfSkipped" || string.IsNullOrEmpty(v)) continue;
            foreach (Match m in Token.Matches(v))
            {
                var n = m.Groups[1].Value.Trim();
                if (skipped.Contains(n)) return n;
            }
        }
        return null;
    }

    public string Expand(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Token.Replace(text, m =>
        {
            var name = m.Groups[1].Value.Trim();
            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
            return Lookup(name, fmt) ?? m.Value;
        });
    }

    string Lookup(string name, string fmt)
    {
        if (vars.TryGetValue(name, out var v))
        {
            return fmt?.ToLowerInvariant() switch
            {
                "upper" => v.ToUpper(),
                "lower" => v.ToLower(),
                "title" => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(v.ToLower()),
                "trim" => v.Trim(),
                _ => v,
            };
        }
        return Builtin(name, fmt);
    }

    string Builtin(string name, string fmt)
    {
        var now = DateTime.Now;
        string Fmt(string def) { try { return now.ToString(string.IsNullOrEmpty(fmt) ? def : fmt); } catch (FormatException) { return now.ToString(def); } }
        string Sp(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        return name.ToLowerInvariant() switch
        {
            "date" => Fmt("yyyy-MM-dd"),
            "time" => Fmt("HH-mm-ss"),
            "datetime" or "now" => Fmt("yyyy-MM-dd_HH-mm-ss"),
            "year" => now.ToString("yyyy"),
            "month" => now.ToString("MM"),
            "monthname" => now.ToString("MMMM"),
            "day" => now.ToString("dd"),
            "weekday" => now.ToString("dddd"),
            "hour" => now.ToString("HH"),
            "minute" => now.ToString("mm"),
            "user" => Environment.UserName,
            "computer" => Environment.MachineName,
            "desktop" => Sp(Environment.SpecialFolder.DesktopDirectory),
            "documents" => Sp(Environment.SpecialFolder.MyDocuments),
            "pictures" => Sp(Environment.SpecialFolder.MyPictures),
            "downloads" => Path.Combine(Sp(Environment.SpecialFolder.UserProfile), "Downloads"),
            "home" => Sp(Environment.SpecialFolder.UserProfile),
            "flowfolder" => flowFolder,
            "copyfolder" => Store() is { } s ? s.FolderPath ?? PackedPrefix : "",
            "loop" => loops.Count > 0 ? loops.Peek().Iter.ToString() : "0",
            "newline" or "nl" => "\r\n",
            "tab" => "\t",
            _ => null,
        };
    }

    public static readonly (string Name, string Meaning)[] BuiltinHelp =
    {
        ("date", "today, e.g. 2026-10-17  (custom: {date:MM-dd-yyyy})"),
        ("time", "now, e.g. 14-05-09"),
        ("datetime", "e.g. 2026-10-17_14-05-09"),
        ("year", ""), ("month", ""), ("monthname", ""), ("day", ""), ("weekday", ""), ("hour", ""), ("minute", ""),
        ("loop", "current round number inside a Repeat"),
        ("user", "your Windows user name"),
        ("computer", "this PC's name"),
        ("desktop", "your Desktop folder"),
        ("documents", "your Documents folder"),
        ("downloads", "your Downloads folder"),
        ("pictures", "your Pictures folder"),
        ("home", "your user folder"),
        ("flowfolder", "the folder this flow (or its .exe) is saved in"),
        ("copyfolder", "the copy folder — the files your program uses"),
        ("newline", "a line break"),
        ("tab", "a tab character"),
    };

    /// <summary>The copy folder: the files packed in the .exe when running one, otherwise the folder set in Flow settings.</summary>
    IFileStore Store()
    {
        if (storeChecked) return store;
        storeChecked = true;
        var set = flow.CopyFolder?.Trim();
        if (string.IsNullOrEmpty(set)) return null;
        var full = ResolveRaw(set);
        return store = Directory.Exists(full) ? new FolderStore(full) : null;
    }

    /// <summary>
    /// Does this source mean copy-folder files? ("copy:…", a path inside the copy folder, or a plain name/pattern
    /// that isn't on disk but matches copy-folder files.) <paramref name="names"/> may be empty = named, but not there.
    /// </summary>
    bool InStore(string raw, out List<string> names)
    {
        names = new List<string>();
        var fs = Store();
        if (fs == null) return false;
        if (raw.StartsWith(PackedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            names = MatchStore(raw[PackedPrefix.Length..]);
            return true;
        }
        if (fs.FolderPath != null && raw.StartsWith(fs.FolderPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
        {
            names = MatchStore(Path.GetRelativePath(fs.FolderPath, raw));
            return true;
        }
        if (!Path.IsPathRooted(raw) && !File.Exists(ResolveRaw(raw)))
        {
            names = MatchStore(raw);
            return names.Count > 0;
        }
        return false;
    }

    /// <summary>
    /// Copy-folder files matching a name spec. An exact name always wins; otherwise the spec is read as patterns:
    /// "steam_api(64).dll" (bracketed part optional), "*.dll", "file?.txt", several separated by ; or |.
    /// </summary>
    List<string> MatchStore(string spec)
    {
        var fs = Store();
        spec = (spec ?? "").Trim().Trim('"').Replace('/', '\\').TrimStart('\\');
        if (fs == null || spec.Length == 0) return new List<string>();
        if (fs.Exists(spec)) return new List<string> { spec };
        var patterns = NamePatterns(spec);
        return fs.Names.Where(n => patterns.Any(p => p.IsMatch(n) || p.IsMatch(Path.GetFileName(n)))).ToList();
    }

    /// <summary>The same pattern rules for a file on disk: "C:\games\steam_api(64).dll", "C:\x\*.dll".</summary>
    static List<string> MatchDisk(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new List<string>();
        var patterns = NamePatterns(Path.GetFileName(fullPath));
        return Directory.EnumerateFiles(dir).Where(f => patterns.Any(p => p.IsMatch(Path.GetFileName(f)))).ToList();
    }

    static List<Regex> NamePatterns(string spec) =>
        spec.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(one =>
            {
                var sb = new StringBuilder("^");
                foreach (var c in one.Replace('/', '\\'))
                    sb.Append(c switch
                    {
                        '*' => @"[^\\]*",
                        '?' => @"[^\\]",
                        '(' => "(?:",
                        ')' => ")?",
                        _ => Regex.Escape(c.ToString()),
                    });
                sb.Append('$');
                try { return new Regex(sb.ToString(), RegexOptions.IgnoreCase); }
                catch (ArgumentException) { return new Regex("^" + Regex.Escape(one) + "$", RegexOptions.IgnoreCase); } // unbalanced brackets: exact name
            })
            .ToList();

    /// <summary>Runs one file's copy; a problem with that file is logged and the rest carry on.</summary>
    void Guard(string name, Action act)
    {
        try { act(); }
        catch (Exception ex)
        {
            result.Errors++;
            host.Log($"✖ {name}: {ex.Message}", LogKind.Error);
        }
    }

    static string TargetName(string source, string rawNewName)
    {
        var fileName = Path.GetFileName(source);
        var newName = (rawNewName ?? "").Trim();
        if (newName.Length == 0) return fileName;
        newName = SafeName(newName);
        return HasRealExtension(newName) ? newName : newName + Path.GetExtension(source);
    }

    /// <summary>Copies ONE copy-folder file to dst (straight out of the .exe if it's packed there).</summary>
    void TransferFromStore(string name, string dst, string policy)
    {
        var fs = Store();
        if (!fs.Exists(name)) { host.Log($"⤼ \"{name}\" isn't in the copy folder — skipped", LogKind.Warn); return; }
        bool replaced = false;
        if (File.Exists(dst))
        {
            if (policy == BlockLibrary.ExSkip) { host.Log($"⤼ {Path.GetFileName(dst)} already exists in destination — skipped", LogKind.Warn); return; }
            if (policy == BlockLibrary.ExKeepBoth) dst = UniquePath(dst);
            else { ClearReadOnly(dst); replaced = true; }
        }
        fs.CopyTo(name, dst);
        result.FilesWritten++;
        host.Log($"⇱ Copied {name} from the {(fs.FolderPath == null ? "program" : "copy folder")} → {dst}{(replaced ? "  (replaced old file)" : "")}", LogKind.Ok);
    }

    /// <summary>"Copy/Move many files" with {copyfolder} as the source: copies each matching file, one at a time.</summary>
    Signal CopyManyFromStore(Block b, string subFolder)
    {
        var fs = Store() ?? throw new InvalidOperationException("This flow has no copy folder.");
        var dest = ResolvePath(b.Get("dest"));
        bool sub = b.GetBool("subfolders");
        var patterns = Expand(b.Get("pattern")).Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns.Length == 0) patterns = new[] { "*.*" };
        var prefix = subFolder.Length == 0 ? "" : subFolder.TrimEnd('\\') + "\\";
        var names = fs.Names
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(n => sub || !n[prefix.Length..].Contains('\\'))
            .Where(n => patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesWin32Expression(
                System.IO.Enumeration.FileSystemName.TranslateWin32Expression(p), Path.GetFileName(n), ignoreCase: true)))
            .ToList();
        if (names.Count == 0) { host.Log($"⇶ No files in the copy folder matched {string.Join(";", patterns)}", LogKind.Warn); return Signal.Next; }
        if (b.Get("action") == "Move" && fs.FolderPath == null) host.Log("(Files inside the program can't be moved out of it — they're copied instead.)", LogKind.Info);
        foreach (var n in names)
        {
            if (StopRequested) break;
            var target = Path.Combine(dest, sub ? n[prefix.Length..] : Path.GetFileName(n));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (b.Get("action") == "Move" && fs.FolderPath != null) Transfer(Path.Combine(fs.FolderPath, n), target, true, b.Get("ifExists"));
                else TransferFromStore(n, target, b.Get("ifExists"));
            }
            catch (Exception ex)
            {
                result.Errors++;
                host.Log($"✖ {n}: {ex.Message}", LogKind.Error);
            }
        }
        return Signal.Next;
    }

    // ================================================================ file helpers

    string ResolvePath(string raw) => ResolveRaw(Expand(raw));

    string ResolveRaw(string expanded)
    {
        var p = (expanded ?? "").Trim().Trim('"').Trim();
        if (p.Length == 0) return flowFolder;
        p = Environment.ExpandEnvironmentVariables(p);
        if (!Path.IsPathRooted(p)) p = Path.Combine(flowFolder, p);
        return Path.GetFullPath(p);
    }

    void Transfer(string src, string dst, bool move, string policy)
    {
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
        {
            host.Log($"⤼ {Path.GetFileName(src)} is already in that spot", LogKind.Warn);
            return;
        }
        bool replaced = false;
        if (File.Exists(dst))
        {
            if (policy == BlockLibrary.ExSkip) { host.Log($"⤼ {Path.GetFileName(dst)} already exists in destination — skipped", LogKind.Warn); return; }
            if (policy == BlockLibrary.ExKeepBoth) dst = UniquePath(dst);
            else { ClearReadOnly(dst); replaced = true; }
        }
        if (move) File.Move(src, dst, overwrite: true);
        else File.Copy(src, dst, overwrite: true);
        result.FilesWritten++;
        host.Log($"{(move ? "➜ Moved" : "⧉ Copied")} {Path.GetFileName(src)} → {dst}{(replaced ? "  (replaced old file)" : "")}", LogKind.Ok);
    }

    static void ClearReadOnly(string path)
    {
        if (File.Exists(path))
        {
            var a = File.GetAttributes(path);
            if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(bad.Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "untitled" : s;
    }

    static bool HasRealExtension(string name) => Regex.IsMatch(name, @"\.[A-Za-z0-9]{1,6}$");
    static string WithTxt(string name) => HasRealExtension(name) ? name : name + ".txt";
    static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    static int ParseInt(string s, int fallback) => int.TryParse(s.Trim(), out var n) ? n : fallback;
    static string Short(string s)
    {
        s = s.Replace("\r\n", " ⏎ ").Replace("\n", " ⏎ ");
        return s.Length > 120 ? s[..117] + "…" : s;
    }
}
