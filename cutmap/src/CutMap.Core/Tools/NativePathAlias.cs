using System.Runtime.InteropServices;
using System.Text;

namespace CutMap.Core.Tools;

/// <summary>
/// 给原生子进程（ctb-tile）用的纯 ASCII 路径别名。
///
/// **为什么需要**：ctb-tile 是原生程序，argv 经 CRT 从 Unicode 转成本地代码页
/// （中文 Windows 上是 GBK）。它内部的部分路径解析走的是 UTF-8 / 宽字符口径，
/// 于是含中文的路径**目录明明存在也会报 "The output directory does not exist"**
/// （实测：<c>D:\...\专利\cutmap</c> 必现，纯 ASCII 路径正常）。
/// .NET 侧无法改变子进程的 argv 转换方式，所以对含非 ASCII 字符的路径：
///
/// · 目录 —— 用 NTFS 目录联接（junction）映射到临时目录下的 ASCII 名；
/// · 文件名本身含非 ASCII —— 硬链接（同卷）或复制一份到临时目录，用 ASCII 名。
///
/// 临时别名随 <see cref="Dispose"/> 删除；目录联接只删除联接本身，不动目标里的瓦片。
/// </summary>
internal sealed class NativePathAlias : IDisposable
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    private readonly string _root;
    private readonly List<string> _junctions = [];
    private readonly List<string> _files = [];
    private int _next;

    /// <summary>所有路径都是 ASCII 时返回 null——不建临时目录，不改变原有行为。</summary>
    public static NativePathAlias? For(params string[] paths) =>
        paths.Any(p => p.Any(c => c > 0x7F)) ? new NativePathAlias() : null;

    private NativePathAlias()
    {
        _root = Path.Combine(Path.GetTempPath(), "cutmap-ascii-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    /// <summary>目录已存在时返回 ASCII 别名，否则原样返回。</summary>
    public string MapDirectory(string path)
    {
        if (!NeedsAlias(path)) return path;
        var target = Path.GetFullPath(path);
        if (!Directory.Exists(target)) return path;

        var link = Path.Combine(_root, "d" + _next++);
        CreateJunction(link, target);
        _junctions.Add(link);
        return link;
    }

    /// <summary>带路径的文件：目录含非 ASCII 就联接目录；文件名本身含非 ASCII 就硬链接/复制。</summary>
    public string MapFile(string path)
    {
        if (!NeedsAlias(path)) return path;
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);

        if (!NeedsAlias(name))
            return Path.Combine(MapDirectory(Path.GetDirectoryName(full)!), name);

        var alias = Path.Combine(_root, "f" + _next++ + SafeExtension(full));
        // 硬链接不占额外空间且即时；跨卷时退回复制（ctb-tile 只需要读）。
        if (!CreateHardLinkW(alias, full, IntPtr.Zero))
            File.Copy(full, alias, overwrite: true);
        _files.Add(alias);
        return alias;
    }

    public void Dispose()
    {
        foreach (var link in _junctions)
            RemoveDirectoryW(link);
        foreach (var file in _files)
        {
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // **不要递归删除**：万一某个联接没删掉，递归删除会顺着联接进到用户的
        // 瓦片目录里。清完联接后临时目录本来就应该空了，非空就原样留着。
        try { Directory.Delete(_root); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static bool NeedsAlias(string path) => path.Any(c => c > 0x7F);

    /// <summary>扩展名保留下来纯属便于排障；含非 ASCII 时不用。</summary>
    private static string SafeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && ext.All(c => c <= 0x7F) ? ext : "";
    }

    private static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);

        // 联接 = 带 IO_REPARSE_TAG_MOUNT_POINT 的重解析点。普通用户即可创建，
        // 不需要符号链接所需的 SeCreateSymbolicLinkPrivilege（管理员/开发者模式）。
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var dataLength = 8 + substitute.Length + 2 + print.Length + 2;

        var buffer = new byte[8 + dataLength];
        using (var ms = new MemoryStream(buffer))
        using (var w = new BinaryWriter(ms))
        {
            w.Write(IO_REPARSE_TAG_MOUNT_POINT);
            w.Write((ushort)dataLength);
            w.Write((ushort)0);                              // Reserved
            w.Write((ushort)0);                              // SubstituteNameOffset
            w.Write((ushort)substitute.Length);
            w.Write((ushort)(substitute.Length + 2));        // PrintNameOffset
            w.Write((ushort)print.Length);
            w.Write(substitute);
            w.Write((ushort)0);
            w.Write(print);
            w.Write((ushort)0);
        }

        var handle = CreateFileW(link, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                                 FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle == new IntPtr(-1))
            throw new IOException($"无法打开目录以创建联接：{link}（错误 {Marshal.GetLastWin32Error()}）");

        try
        {
            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length,
                                 IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"无法为含中文的路径创建 ASCII 联接：{target}（错误 {error}）。" +
                    "请把输入/输出放到本地 NTFS 磁盘，或改用纯英文路径。");
            }
        }
        catch
        {
            RemoveDirectoryW(link);
            throw;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(
        string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool RemoveDirectoryW(string pathName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
