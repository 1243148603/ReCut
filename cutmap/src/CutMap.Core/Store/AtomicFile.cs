namespace CutMap.Core.Store;

public static class AtomicFile
{
    public static void Write(string path, string text)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            // Windows 的索引器/杀毒软件可能在文件刚写完时短暂占用目标文件。
            // 保持同一个临时文件并有限重试，避免一次瞬时共享冲突让配置操作失败。
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
