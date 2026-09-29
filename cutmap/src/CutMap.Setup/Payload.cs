using System.IO.Compression;
using System.Text;

namespace CutMap.Setup;

/// <summary>
/// 安装器自带的载荷：一段追加在 exe 字节后面的 zip。
///
/// 文件布局：
/// <code>
///   [ 安装器 exe ][ payload.zip ][ int64 载荷长度 ][ 8 字节魔数 ]
/// </code>
///
/// 魔数用来判断"这个 exe 到底带没带载荷"——开发时直接 `dotnet run` 出来的
/// 安装器没有载荷，得给一句人话提示，而不是让人对着解压失败发懵。
/// </summary>
internal static class Payload
{
    private const long MaxPayloadBytes = 4L * 1024 * 1024 * 1024;   // 4 GB，防御性上限

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CUTMAPPA");
    private const int TrailerBytes = 8 + 8;                          // 长度 + 魔数

    /// <summary>本 exe 是否带载荷。</summary>
    public static bool IsPresent(out string reason)
    {
        try
        {
            using var file = File.OpenRead(Environment.ProcessPath!);
            if (file.Length < TrailerBytes + 1)
            {
                reason = "这个 exe 太短，不像是带载荷的安装包。";
                return false;
            }

            file.Seek(-TrailerBytes, SeekOrigin.End);
            var length = ReadInt64(file);
            var magic = new byte[8];
            file.ReadExactly(magic);

            if (!magic.SequenceEqual(Magic))
            {
                reason = "这个 exe 末尾没有安装载荷。\n\n" +
                         "它可能是 `dotnet run` 或 `dotnet build` 的产物，而不是" +
                         "由 pack-installer.ps1 打出来的安装包。";
                return false;
            }
            if (length <= 0 || length > MaxPayloadBytes || length > file.Length - TrailerBytes)
            {
                reason = $"载荷长度不合法（{length} 字节），安装包可能损坏。";
                return false;
            }

            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"读不出安装载荷：{ex.Message}";
            return false;
        }
    }

    /// <summary>把载荷解压到 <paramref name="targetDirectory"/>。返回解压出的文件数。</summary>
    public static int ExtractTo(string targetDirectory, IProgress<(int Done, int Total, string Name)>? progress)
    {
        Directory.CreateDirectory(targetDirectory);

        using var file = File.OpenRead(Environment.ProcessPath!);
        file.Seek(-TrailerBytes, SeekOrigin.End);
        var length = ReadInt64(file);
        var payloadStart = file.Length - TrailerBytes - length;

        file.Seek(payloadStart, SeekOrigin.Begin);
        using var slice = new SubStream(file, payloadStart, length);
        using var zip = new ZipArchive(slice, ZipArchiveMode.Read);

        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToArray();
        var root = Path.GetFullPath(targetDirectory);

        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

            // Zip Slip：压缩包里的路径可能带 ../ 指到目标目录之外。
            // 载荷是我们自己打的，但安装器会被别人拿去用，这一道必须留着。
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"载荷里的路径越出了安装目录：{entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);

            if (i % 200 == 0 || i == entries.Length - 1)
                progress?.Report((i + 1, entries.Length, entry.Name));
        }

        return entries.Length;
    }

    private static long ReadInt64(Stream stream)
    {
        var buffer = new byte[8];
        stream.ReadExactly(buffer);
        return BitConverter.ToInt64(buffer, 0);
    }

    /// <summary>
    /// 文件里的一段只读视图，坐标从 0 开始（即把 <paramref name="start"/> 处当作流开头）。
    ///
    /// ZipArchive 要能随意 Seek——它先跳到尾部读中央目录，再跳回去读条目。
    /// 所以每次 Seek **必须把底层 FileStream 一起挪过去**。
    /// 最初只记了个 _position 而没动 inner，顺序读看不出来，一跳转就全错，
    /// 报的是 "End of Central Directory record could not be found"。
    /// </summary>
    private sealed class SubStream(FileStream inner, long start, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0) return 0;
            var toRead = (int)Math.Min(count, remaining);
            var read = inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => _position,
            };
            inner.Seek(start + _position, SeekOrigin.Begin);
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // inner 由调用方持有，这里不关。
            base.Dispose(disposing);
        }
    }
}
