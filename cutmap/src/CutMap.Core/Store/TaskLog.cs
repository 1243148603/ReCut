namespace CutMap.Core.Store;

public sealed class TaskLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    public string FilePath { get; }
    public TaskLog(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _writer = new StreamWriter(FilePath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
    }
    public void Write(string line) { lock (_gate) _writer.WriteLine($"[{DateTimeOffset.Now:O}] {line}"); }
    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
