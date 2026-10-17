using System.Text;

namespace BlockFlow;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string file = args.FirstOrDefault(a => !a.StartsWith("--"));
        bool run = args.Any(a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        bool edit = args.Any(a => a.Equals("--edit", StringComparison.OrdinalIgnoreCase));

        // BlockFlow.exe --build "project.bflow|.exe" "output.exe" : same as Save .exe, without the window.
        int bi = Array.FindIndex(args, a => a.Equals("--build", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            if (args.Length < bi + 3) return 2;
            try
            {
                var f = Flow.Load(args[bi + 1]);
                bool fromExe = args[bi + 1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                bool folderHere = !string.IsNullOrWhiteSpace(f.CopyFolder) && Directory.Exists(f.CopyFolder);
                ExeBuilder.Build(f, args[bi + 2], fromExe && !folderHere ? args[bi + 1] : null);
                return 0;
            }
            catch { return 1; }
        }

        // A flow saved as its own .exe: just run it (or open the editor with --edit).
        var embedded = ExeBuilder.ReadSelf();
        if (embedded != null)
        {
            if (!edit)
            {
                // The program's own files stay packed inside it; blocks copy out one file at a time when needed.
                PackedFiles packed = null;
                try { packed = PackedFiles.Open(Environment.ProcessPath); }
                catch (Exception ex) { MessageBox.Show("Couldn't read the files packed in this program:\n\n" + ex.Message, embedded.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                using (packed)
                    return RunWithoutEditor(embedded, Path.GetDirectoryName(Environment.ProcessPath), packed);
            }
            file ??= Environment.ProcessPath;
        }

        if (run && file != null)
        {
            Flow flow;
            try { flow = Flow.Load(file); }
            catch (Exception ex)
            {
                MessageBox.Show($"Couldn't open the flow \"{file}\".\n\n{ex.Message}", "BlockFlow", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            return RunWithoutEditor(flow, Path.GetDirectoryName(Path.GetFullPath(file)));
        }
        Application.Run(new MainForm(file));
        return 0;
    }

    /// <summary>Used by flow .exe files and shortcuts: runs the flow showing only the question boxes.</summary>
    static int RunWithoutEditor(Flow flow, string folder, IFileStore packed = null)
    {
        var host = new QuietHost();
        var result = new Runner(flow, host, folder, packed).Run();
        var logPath = host.SaveLog();

        if (result.Outcome == RunOutcome.Invalid)
            MessageBox.Show($"This flow has problems. Open it in BlockFlow to fix them.\n\nLog: {logPath}", flow.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (result.Errors > 0)
            MessageBox.Show($"Finished, but {result.Errors} thing(s) went wrong.\n\nDetails are in:\n{logPath}", flow.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else if (flow.ShowDoneMessage && result.Outcome is RunOutcome.Finished or RunOutcome.StoppedByBlock)
            MessageBox.Show($"Done! {result.FilesWritten} file(s) written, copied or moved.", flow.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return result.Errors > 0 ? 1 : 0;
    }

    sealed class QuietHost : IRunHost
    {
        readonly StringBuilder log = new();

        public AskResult Ask(AskRequest request) => AskDialog.Show(null, request);
        public void Log(string message, LogKind kind) => log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{kind}]  {message}");
        public void Current(int index) { }
        public void ShowMessage(string title, string text) =>
            MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);

        public string SaveLog()
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockFlow", "last-run.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, log.ToString());
            }
            catch { }
            return path;
        }
    }
}
