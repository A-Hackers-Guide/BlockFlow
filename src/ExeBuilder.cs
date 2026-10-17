using System.IO.Compression;
using System.Text;

namespace BlockFlow;

/// <summary>
/// Turns a flow into its own .exe: a copy of BlockFlow.exe with the project appended.
/// v2 layout at the end of the file: [flow json][zip of the copy folder][int64 json length][int64 zip length][magic "BLOCKFLOW-FLOW02"].
/// v1 (older builds):                [flow json][int64 json length][magic "BLOCKFLOW-FLOW01"].
/// </summary>
public static class ExeBuilder
{
    static readonly byte[] MagicV1 = "BLOCKFLOW-FLOW01"u8.ToArray();
    static readonly byte[] MagicV2 = "BLOCKFLOW-FLOW02"u8.ToArray();
    const long MaxJson = 64L * 1024 * 1024;

    /// <summary>Only the published single-file BlockFlow.exe can be copied into a working program.</summary>
#pragma warning disable IL3000 // empty Location is exactly how we detect the single-file exe
    public static bool CanBuild => string.IsNullOrEmpty(typeof(ExeBuilder).Assembly.Location);
#pragma warning restore IL3000

    internal sealed class Payload
    {
        public long BaseLength;
        public string Json;
        public long ZipOffset, ZipLength;
    }

    public static bool HasProject(string exePath)
    {
        try { return Inspect(exePath)?.Json != null; } catch { return false; }
    }

    public static Flow ReadEmbedded(string exePath)
    {
        var p = Inspect(exePath);
        return p?.Json == null ? null : Flow.FromJson(p.Json);
    }

    public static Flow ReadSelf()
    {
        try { return ReadEmbedded(Environment.ProcessPath); }
        catch { return null; }
    }

    internal static Payload Inspect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long len = fs.Length;
        var none = new Payload { BaseLength = len };
        if (len < 24) return none;

        var magic = new byte[16];
        fs.Seek(len - 16, SeekOrigin.Begin);
        fs.ReadExactly(magic);

        long jsonLen, zipLen = 0, trailer;
        if (magic.AsSpan().SequenceEqual(MagicV2))
        {
            if (len < 32) return none;
            var nums = new byte[16];
            fs.Seek(len - 32, SeekOrigin.Begin);
            fs.ReadExactly(nums);
            jsonLen = BitConverter.ToInt64(nums, 0);
            zipLen = BitConverter.ToInt64(nums, 8);
            trailer = 32;
        }
        else if (magic.AsSpan().SequenceEqual(MagicV1))
        {
            var num = new byte[8];
            fs.Seek(len - 24, SeekOrigin.Begin);
            fs.ReadExactly(num);
            jsonLen = BitConverter.ToInt64(num, 0);
            trailer = 24;
        }
        else return none;

        if (jsonLen <= 0 || jsonLen > MaxJson || zipLen < 0 || jsonLen + zipLen > len - trailer) return none;
        long start = len - trailer - zipLen - jsonLen;
        var json = new byte[jsonLen];
        fs.Seek(start, SeekOrigin.Begin);
        fs.ReadExactly(json);
        return new Payload { BaseLength = start, Json = Encoding.UTF8.GetString(json), ZipOffset = start + jsonLen, ZipLength = zipLen };
    }

    /// <summary>Writes the flow (and every file in its copy folder, if it has one) into a new .exe.</summary>
    public static int Build(Flow flow, string outPath, string carryFilesFrom = null)
    {
        var self = Environment.ProcessPath;
        outPath = Path.GetFullPath(outPath);
        if (string.Equals(Path.GetFullPath(self), outPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("That's the program that is running right now, so it can't be overwritten. Pick a different name or folder.");

        var baseLength = Inspect(self).BaseLength; // strip any project already inside this exe
        var json = Encoding.UTF8.GetBytes(flow.ToJson());
        var tmp = outPath + ".tmp";
        var zipTmp = Path.Combine(Path.GetTempPath(), "blockflow-" + Guid.NewGuid().ToString("N") + ".zip");
        int fileCount = 0;
        try
        {
            var folder = flow.CopyFolder?.Trim();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                fileCount = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
                if (fileCount > 0) ZipFile.CreateFromDirectory(folder, zipTmp, CompressionLevel.Optimal, includeBaseDirectory: false);
            }

            else if (carryFilesFrom != null && File.Exists(carryFilesFrom) && Inspect(carryFilesFrom) is { ZipLength: > 0 } old)
            {
                // Copy the packed files across from the .exe this project was opened from — no unpacking.
                using (var from = new FileStream(carryFilesFrom, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var to = new FileStream(zipTmp, FileMode.Create, FileAccess.Write))
                {
                    from.Seek(old.ZipOffset, SeekOrigin.Begin);
                    CopyBytes(from, to, old.ZipLength);
                }
                using var pf = PackedFiles.Open(carryFilesFrom);
                fileCount = pf?.Count ?? 0;
            }

            using (var src = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                CopyBytes(src, dst, baseLength);
                dst.Write(json);
                long zipLen = 0;
                if (File.Exists(zipTmp))
                {
                    using var z = File.OpenRead(zipTmp);
                    zipLen = z.Length;
                    z.CopyTo(dst);
                }
                dst.Write(BitConverter.GetBytes((long)json.Length));
                dst.Write(BitConverter.GetBytes(zipLen));
                dst.Write(MagicV2);
            }
            File.Move(tmp, outPath, overwrite: true);
            return fileCount;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (File.Exists(zipTmp)) File.Delete(zipTmp);
        }
    }

    static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[1 << 20];
        while (count > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            dst.Write(buf, 0, n);
            count -= n;
        }
    }
}

/// <summary>Where the copy-folder files come from while a flow runs: a real folder, or the files packed inside a flow .exe.</summary>
public interface IFileStore : IDisposable
{
    bool Exists(string name);
    void CopyTo(string name, string destPath);
    IEnumerable<string> Names { get; }
    /// <summary>A real folder path, or null when the files are packed inside the .exe.</summary>
    string FolderPath { get; }
}

public sealed class FolderStore(string folder) : IFileStore
{
    public string FolderPath => folder;
    public bool Exists(string name) => File.Exists(Path.Combine(folder, Norm(name)));
    public void CopyTo(string name, string destPath) => File.Copy(Path.Combine(folder, Norm(name)), destPath, overwrite: true);
    public IEnumerable<string> Names => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f));
    static string Norm(string n) => n.Replace('/', '\\').TrimStart('\\');
    public void Dispose() { }
}

/// <summary>
/// Reads the copy-folder files packed inside a flow .exe. Nothing is unpacked up front:
/// each file is copied straight out of the .exe only when a block asks for it.
/// </summary>
public sealed class PackedFiles : IFileStore
{
    readonly FileStream file;
    readonly ZipArchive zip;
    readonly Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);

    PackedFiles(FileStream file, long offset, long length)
    {
        this.file = file;
        zip = new ZipArchive(new SubStream(file, offset, length), ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
            if (!string.IsNullOrEmpty(e.Name)) entries[Key(e.FullName)] = e; // skip folder entries
    }

    /// <summary>Null if the .exe has no packed files.</summary>
    public static PackedFiles Open(string exePath)
    {
        var p = ExeBuilder.Inspect(exePath);
        if (p?.Json == null || p.ZipLength == 0) return null;
        var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try { return new PackedFiles(fs, p.ZipOffset, p.ZipLength); }
        catch { fs.Dispose(); throw; }
    }

    static string Key(string name) => name.Replace('\\', '/').Trim().TrimStart('/');

    public int Count => entries.Count;
    public string FolderPath => null;
    public IEnumerable<string> Names => entries.Values.Select(e => e.FullName.Replace('/', '\\'));
    public bool Exists(string name) => entries.ContainsKey(Key(name));

    public void CopyTo(string name, string destPath)
    {
        if (!entries.TryGetValue(Key(name), out var e)) throw new FileNotFoundException($"\"{name}\" isn't packed in this program.");
        var tmp = destPath + ".bftmp";
        try
        {
            using (var input = e.Open())
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                input.CopyTo(output);
            File.Move(tmp, destPath, overwrite: true);
            File.SetLastWriteTime(destPath, e.LastWriteTime.LocalDateTime);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public void Dispose()
    {
        zip.Dispose();
        file.Dispose();
    }

    /// <summary>A read-only window onto part of the .exe (the packed zip), so it can be read without copying it out.</summary>
    sealed class SubStream(Stream inner, long start, long length) : Stream
    {
        long pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => pos; set => pos = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long left = length - pos;
            if (left <= 0) return 0;
            if (count > left) count = (int)left;
            inner.Seek(start + pos, SeekOrigin.Begin);
            int n = inner.Read(buffer, offset, count);
            pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => pos + offset,
            _ => length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
