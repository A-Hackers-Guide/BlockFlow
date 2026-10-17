using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlockFlow;

public enum FieldKind { Text, Multi, Var, Bool, Choice, File, Folder, Number }

/// <summary>How a block behaves structurally in the script.</summary>
public enum BlockShape { Normal, IfStart, Else, IfEnd, LoopStart, LoopEnd }

public sealed class FieldDef
{
    public string Key = "", Label = "", Hint = "", Default = "";
    public FieldKind Kind;
    public string[] Choices = Array.Empty<string>();
    public Func<Block, bool> VisibleIf;
}

public sealed class BlockDef
{
    public string Type = "", Name = "", Category = "", Icon = "", Help = "";
    public Color Color;
    public BlockShape Shape;
    public List<FieldDef> Fields = new();
    public Func<Block, string> Summary = _ => "";
    /// <summary>For container blocks: the closing block that is added automatically.</summary>
    public string PairType;

    public bool IsContainer => Shape is BlockShape.IfStart or BlockShape.LoopStart;
    public bool IsCloser => Shape is BlockShape.IfEnd or BlockShape.LoopEnd;
}

public sealed class Block
{
    public string Type { get; set; } = "";
    public bool Disabled { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();

    [JsonIgnore] public BlockDef Def => BlockLibrary.Get(Type);

    public string Get(string key)
    {
        if (Values.TryGetValue(key, out var v) && v != null) return v;
        return Def?.Fields.FirstOrDefault(f => f.Key == key)?.Default ?? "";
    }

    public bool GetBool(string key) => Get(key) == "true";
    public void Set(string key, string value) => Values[key] = value ?? "";

    public Block Clone() => new() { Type = Type, Disabled = Disabled, Values = new(Values) };

    public static Block Create(string type)
    {
        var b = new Block { Type = type };
        foreach (var f in BlockLibrary.Get(type).Fields) b.Values[f.Key] = f.Default;
        return b;
    }
}

public sealed class Flow
{
    public string App { get; set; } = "BlockFlow";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "My Flow";
    public bool ShowDoneMessage { get; set; } = true;
    /// <summary>Where "Copy to the copy folder" blocks copy to. Empty = ask when the flow runs.</summary>
    public string CopyFolder { get; set; } = "";
    public List<Block> Blocks { get; set; } = new();

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Opts);

    public static Flow FromJson(string json)
    {
        var f = JsonSerializer.Deserialize<Flow>(json, Opts) ?? throw new InvalidDataException("The file is empty.");
        f.Blocks ??= new();
        f.CopyFolder ??= "";
        f.Blocks.RemoveAll(b => b == null || !BlockLibrary.Exists(b.Type));
        foreach (var b in f.Blocks) b.Values ??= new();
        return f;
    }

    public static Flow Load(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? ExeBuilder.ReadEmbedded(path) ?? throw new InvalidDataException("That .exe doesn't contain a BlockFlow flow.")
            : FromJson(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ToJson());

    public Flow DeepCopy() => FromJson(ToJson());

    public static string BlocksToJson(List<Block> blocks) => JsonSerializer.Serialize(blocks);
    public static List<Block> BlocksFromJson(string json) => JsonSerializer.Deserialize<List<Block>>(json) ?? new();

    /// <summary>Every variable name a block in this flow can create.</summary>
    public IEnumerable<string> DefinedVariables() =>
        Blocks.Where(b => b.Def?.Fields.Any(f => f.Key == "var" && f.Kind == FieldKind.Var && b.Def.Shape == BlockShape.Normal) == true)
              .Select(b => Runner.CleanVarName(b.Get("var")))
              .Where(v => v.Length > 0)
              .Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Matches If/Else/End and Repeat/End blocks and works out nesting depth.</summary>
public sealed class Structure
{
    public int[] Depth, Match, ElseOf;
    public bool[] EffectivelyDisabled;
    public readonly List<(int Index, string Message)> Errors = new();

    public string ErrorFor(int i) => Errors.FirstOrDefault(e => e.Index == i).Message;

    public static Structure Analyze(IList<Block> blocks)
    {
        int n = blocks.Count;
        var s = new Structure { Depth = new int[n], Match = new int[n], ElseOf = new int[n], EffectivelyDisabled = new bool[n] };
        Array.Fill(s.Match, -1);
        Array.Fill(s.ElseOf, -1);
        var stack = new Stack<int>();
        int disabledDepth = 0; // number of disabled containers we're inside

        for (int i = 0; i < n; i++)
        {
            var shape = blocks[i].Def.Shape;
            BlockShape TopShape() => stack.Count > 0 ? blocks[stack.Peek()].Def.Shape : BlockShape.Normal;
            s.EffectivelyDisabled[i] = blocks[i].Disabled || disabledDepth > 0;

            switch (shape)
            {
                case BlockShape.IfStart:
                case BlockShape.LoopStart:
                    s.Depth[i] = stack.Count;
                    stack.Push(i);
                    if (blocks[i].Disabled) disabledDepth++;
                    break;

                case BlockShape.Else:
                    if (TopShape() == BlockShape.IfStart && s.ElseOf[stack.Peek()] < 0)
                    {
                        s.Depth[i] = stack.Count - 1;
                        s.ElseOf[stack.Peek()] = i;
                    }
                    else
                    {
                        s.Depth[i] = stack.Count;
                        s.Errors.Add((i, "\"Else\" must be inside an If block (and only one per If)."));
                    }
                    break;

                case BlockShape.IfEnd:
                case BlockShape.LoopEnd:
                    var want = shape == BlockShape.IfEnd ? BlockShape.IfStart : BlockShape.LoopStart;
                    if (TopShape() == want)
                    {
                        int start = stack.Pop();
                        s.Depth[i] = stack.Count;
                        s.Match[start] = i;
                        s.Match[i] = start;
                        if (s.ElseOf[start] >= 0) s.Match[s.ElseOf[start]] = i;
                        if (blocks[start].Disabled) disabledDepth--;
                        s.EffectivelyDisabled[i] = blocks[i].Disabled || blocks[start].Disabled || disabledDepth > 0;
                    }
                    else
                    {
                        s.Depth[i] = stack.Count;
                        s.Errors.Add((i, shape == BlockShape.IfEnd
                            ? "This \"End If\" has no matching If above it."
                            : "This \"End Repeat\" has no matching Repeat above it."));
                    }
                    break;

                default:
                    s.Depth[i] = stack.Count;
                    if (blocks[i].Type == "ExitRepeat" && !stack.Any(k => blocks[k].Def.Shape == BlockShape.LoopStart))
                        s.Errors.Add((i, "\"Exit Repeat\" only works inside a Repeat block."));
                    break;
            }
        }

        foreach (var open in stack)
            s.Errors.Add((open, blocks[open].Def.Shape == BlockShape.IfStart
                ? "This If is missing its \"End If\"."
                : "This Repeat is missing its \"End Repeat\"."));
        return s;
    }
}
