using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CutMap.Core.Tools;

/// <summary>CTB 运行时自包含包的位置（NFR-02）。默认取仓库里的 <c>ctb\</c>。</summary>
public sealed record CtbHome(string Root)
{
    public string ExePath => Path.Combine(Root, "bin", "ctb-tile.exe");
    public string ProjData => Path.Combine(Root, "share", "proj");
    public string GdalData => Path.Combine(Root, "share", "gdal");

    /// <summary>仓库内 <c>ctb\</c> 的默认位置。开发期用；打包后由 App 侧改写。</summary>
    public static CtbHome Default(string repoRoot) => new(Path.Combine(repoRoot, "ctb"));

    /// <summary>
    /// 从可执行文件所在目录逐级往上找 <c>ctb\</c>。
    ///
    /// 开发期 exe 在 <c>cutmap\src\CutMap.App\bin\Debug\net8.0-windows\</c>，
    /// 打包后 <c>ctb\</c> 会与 exe 同级——两种情况都能靠"往上找"覆盖，
    /// 所以这里不写死相对层数。
    /// </summary>
    public static CtbHome? Locate(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = new CtbHome(Path.Combine(dir.FullName, "ctb"));
            if (File.Exists(candidate.ExePath)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public bool IsValid(out string reason)
    {
        if (!File.Exists(ExePath)) { reason = $"找不到 {ExePath}"; return false; }
        if (!Directory.Exists(ProjData)) { reason = $"找不到 PROJ 数据目录 {ProjData}"; return false; }
        if (!Directory.Exists(GdalData)) { reason = $"找不到 GDAL 数据目录 {GdalData}"; return false; }
        reason = "";
        return true;
    }
}

public sealed record CtbTileRequest(
    string InputPath,
    string OutputDirectory,
    /// <summary>深端（最细层）。null = 由 CTB 按分辨率自动决定。</summary>
    int? StartZoom = null,
    /// <summary>浅端（最粗层）。null = 0。</summary>
    int? EndZoom = null);

public sealed record CtbTileResult(
    /// <summary>进程的真实退出码。**不可单独用于判成败**——见类型注释。</summary>
    int RawExitCode,
    /// <summary>修正后的退出码：检出 stderr 错误而原始码为 0 时置 -1，避免误判成功。</summary>
    int ExitCode,
    TimeSpan Elapsed,
    bool SawError,
    string? ErrorLine,
    IReadOnlyList<string> TempFilesLeft)
{
    public bool Succeeded => !SawError && RawExitCode == 0;
}

/// <summary>
/// 以子进程调用 <c>ctb-tile.exe</c> 切片。
///
/// **为什么直接调 exe 而不是 <c>ctb.cmd</c>**：批处理有两条硬约束——必须纯 ASCII
/// （GBK 控制台下 UTF-8 中文注释会被解析错位并当成命令执行）、必须 CRLF
/// （LF-only 会让 <c>goto :label</c> 按错误字节偏移找标签而失效）。直接
/// <see cref="Process"/> 调 exe 并显式设好 PROJ_DATA / GDAL_DATA 可以完全绕开这两个坑。
///
/// **为什么不能只看退出码**：ctb-tile 有两条失败路径，退出码行为不一致。
/// 见 CTB 源码 <c>tools/ctb-tile.cpp</c> 的 <c>runTiler()</c>：
///
/// <code>
///   if (poDataset == NULL) { cerr &lt;&lt; "Error: could not open GDAL dataset"; return 1; }
///   try { ...切片... }
///   catch (CTBException &amp;e) { cerr &lt;&lt; "Error: " &lt;&lt; e.what(); }   // 只打印
///   ...
///   return 0;                                              // ← 仍然返回 0
/// </code>
///
/// 打开失败返回 1；而**切片过程中抛异常时，打印完 <c>Error:</c> 后照样返回 0**——
/// 退出码是成功的，瓦片却是坏的。两条路径的共同点是都往 stderr 打 <c>Error:</c>，
/// 所以以 stderr 为准，退出码仅作参考。
/// </summary>
public static class CtbTileRunner
{
    /// <summary>
    /// GDALTermProgress 的形状是 <c>0...10...20...90...100 - done in 00:03:21</c>：
    /// 中间每一步后面跟省略号，但**最后一步 100 后面没有省略号**，直接接 " - done"。
    /// 只匹配省略号会漏掉终值，进度条永远停在 90%。
    /// </summary>
    private static readonly Regex PercentPattern =
        new(@"(\d{1,3})(?:\.\.\.|\s*-\s*done)", RegexOptions.Compiled);
    private static readonly Regex ErrorPattern = new(@"^\s*Error\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static CtbTileResult Run(
        CtbHome home,
        CtbTileRequest request,
        Action<double>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken cancellation = default)
    {
        if (!home.IsValid(out var reason))
            throw new InvalidOperationException(reason);
        if (!File.Exists(request.InputPath))
            throw new FileNotFoundException("输入栅格不存在", request.InputPath);

        // ctb-tile 在输出目录不存在时会报错，不会自己建。
        Directory.CreateDirectory(request.OutputDirectory);

        var args = new List<string> { "-o", request.OutputDirectory };
        // 注意：-s/-e 才是层级；-m 是 --warp-memory（内存上限），教程 §5.3 写作最大级别是笔误。
        if (request.StartZoom is { } s) { args.Add("-s"); args.Add(s.ToString()); }
        if (request.EndZoom is { } e) { args.Add("-e"); args.Add(e.ToString()); }
        args.Add(request.InputPath);

        var psi = new ProcessStartInfo
        {
            FileName = home.ExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = home.Root,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // 缺 PROJ_DATA 时每次调用都会报 "Cannot find proj.db" 并失败：
        // proj.db 在 share\proj\ 里，不在 DLL 旁边，光拷 DLL 不够。
        psi.Environment["PROJ_DATA"] = home.ProjData;
        psi.Environment["GDAL_DATA"] = home.GdalData;

        var sawError = false;
        var stderrLineCount = 0;
        string? errorLine = null;
        var stderrBuffer = new StringBuilder();
        var lastPercent = -1;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderrBuffer)
            {
                stderrBuffer.AppendLine(e.Data);
                if (!sawError && ErrorPattern.IsMatch(e.Data)) { sawError = true; errorLine = e.Data; }
                stderrLineCount++;
            }
            onLog?.Invoke(e.Data);
        };

        var started = Stopwatch.StartNew();
        process.Start();
        process.BeginErrorReadLine();

        // ctb-tile 的进度由 GDALTermProgress 输出：同一行不断追加 "0...10...20..."，
        // 不换行。所以不能按行读，要按块读并在缓冲里增量匹配百分比。
        var stdout = process.StandardOutput;
        var chunk = new char[256];
        var tail = new StringBuilder();
        try
        {
            while (true)
            {
                var n = stdout.Read(chunk, 0, chunk.Length);
                if (n <= 0) break;
                tail.Append(chunk, 0, n);

                foreach (Match m in PercentPattern.Matches(tail.ToString()))
                {
                    if (!int.TryParse(m.Groups[1].Value, out var pct)) continue;
                    if (pct == lastPercent) continue;
                    lastPercent = pct;
                    onProgress?.Invoke(Math.Clamp(pct / 100.0, 0.0, 1.0));
                }

                // 只保留尾部，防止长任务把内存吃光；100 足够覆盖 "NNN..." 这类匹配。
                if (tail.Length > 512) tail.Remove(0, tail.Length - 256);

                if (cancellation.IsCancellationRequested) break;
            }
        }
        catch (IOException)
        {
            // 进程被 Kill 时管道会断，属预期路径。
        }

        if (cancellation.IsCancellationRequested && !process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        process.WaitForExit();
        started.Stop();

        var leftovers = CleanupTempFiles(request.OutputDirectory);

        // 退出码不可信（见类型注释），以 stderr 为准。
        var rawExit = process.ExitCode;
        var effectiveExit = sawError && rawExit == 0 ? -1 : rawExit;
        if (stderrLineCount == 0 && rawExit != 0 && !sawError)
            errorLine = $"进程退出码 {rawExit}，无 stderr 输出";
        if (sawError && errorLine is null) errorLine = "(stderr 含 Error: 但未捕获到原文)";

        return new CtbTileResult(rawExit, effectiveExit, started.Elapsed, sawError, errorLine, leftovers);
    }

    /// <summary>
    /// 清理 WARP 中间产物。ctb-tile 正常结束不会留下这些；被硬杀（取消）时会残留。
    /// </summary>
    private static List<string> CleanupTempFiles(string directory)
    {
        var left = new List<string>();
        if (!Directory.Exists(directory)) return left;

        foreach (var pattern in new[] { "*.tmp", "*.warp.tmp", "*.tif.tmp" })
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                try { File.Delete(file); }
                catch (IOException) { left.Add(file); }
                catch (UnauthorizedAccessException) { left.Add(file); }
            }
        }
        return left;
    }
}
