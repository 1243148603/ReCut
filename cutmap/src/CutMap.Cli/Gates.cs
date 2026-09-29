
using System.Text.RegularExpressions;
using CutMap.Core.Geo;
using CutMap.Core.Tools;
using CutMap.Core.Terrain;
using OSGeo.GDAL;

namespace CutMap.Cli;

/// <summary>
/// M0 的一致性闸门。这两道不过，后面的里程碑全部无意义。
///
/// 闸门一：用 GDAL 绑定跑「30 幅 → 重庆范围」拼接裁剪，Checksum 必须等于基线的 58775。
///         绑定用的是 MaxRev 的 GDAL 3.13.3 构建，与基线 conda geo 的 3.13.3 同版本号，
///         但**不是同一份二进制**——这一步就是验证两者的 warp 结果确实一致。
/// </summary>
internal static class Gates
{
    private const string BaselineChecksum = "58775";
    private const int ExpectedWidth = 17640;
    private const int ExpectedHeight = 14760;

    /// <summary>教程 §4.2 的裁剪范围（重庆全域预设）。</summary>
    private static readonly BBox Chongqing = new(105.3, 28.1, 110.2, 32.2);

    public static int WarpGate(string repoRoot, string outputPath)
    {
        var demDir = Path.Combine(repoRoot, "data", "inputs", "dem");
        if (!Directory.Exists(demDir))
        {
            Console.Error.WriteLine($"FAIL  找不到 DEM 目录：{demDir}");
            return 1;
        }

        // 显式排序，保证输入顺序可复现（教程按 N28_E105..N32_E110 顺序列出）。
        var sources = Directory.GetFiles(demDir, "*.tif").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"输入 {sources.Length} 幅：{demDir}");

        if (sources.Length != 30)
        {
            Console.Error.WriteLine($"FAIL  期望 30 幅，实际 {sources.Length} 幅。" +
                                    "教程 §3.1 的清单只有 28 幅，会留下 2°×1° 空洞且不报错。");
            return 1;
        }

        Console.WriteLine("命令：" + Mosaic.DescribeCommandLine(sources, outputPath, Chongqing));

        var started = DateTime.UtcNow;
        try
        {
            Mosaic.Run(sources, outputPath, Chongqing);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  warp 抛异常：{ex.Message}");
            return 1;
        }

        var elapsed = DateTime.UtcNow - started;
        Console.WriteLine($"完成，用时 {elapsed.TotalSeconds:F1} s");

        // ---- 核对 ----
        GdalRuntime.EnsureReady();
        using var ds = Gdal.OpenEx(outputPath, (uint)Access.GA_ReadOnly, null, null, null);
        if (ds is null)
        {
            Console.Error.WriteLine($"FAIL  产物打不开：{outputPath}");
            return 1;
        }

        var width = ds.RasterXSize;
        var height = ds.RasterYSize;
        var checksum = Checksum(ds);

        var ok = true;
        ok &= Check("尺寸", $"{width}x{height}", $"{ExpectedWidth}x{ExpectedHeight}",
                    width == ExpectedWidth && height == ExpectedHeight);
        ok &= Check("Checksum", checksum ?? "(无)", BaselineChecksum, checksum == BaselineChecksum);

        Console.WriteLine(ok ? "PASS  闸门一：与基线一致" : "FAIL  闸门一：与基线不一致");
        return ok ? 0 : 1;
    }

    /// <summary>取 gdalinfo -checksum 的 Checksum 值。GDALInfo 是绑定的，不必起子进程。</summary>
    private static string? Checksum(Dataset ds)
    {
        using var opts = new GDALInfoOptions(["-checksum"]);
        var text = Gdal.GDALInfo(ds, opts);
        var m = Regex.Match(text, @"Checksum=(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 闸门二：ctb-tile 子进程。要验三件事——环境变量定位对（否则 proj.db 报错）、
    /// 进度能实时读到、以及**退出码陷阱确实被抓住**（失败时退出码是 0，只能靠 stderr）。
    /// </summary>
    public static int CtbGate(string repoRoot)
    {
        var home = CtbHome.Default(repoRoot);
        if (!home.IsValid(out var why))
        {
            Console.Error.WriteLine($"FAIL  CTB 包不完整：{why}");
            return 1;
        }
        Console.WriteLine($"CTB: {home.ExePath}");

        var workDir = Path.Combine(repoRoot, "cutmap", "_gate", "ctb");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
        Directory.CreateDirectory(workDir);

        // ---- 正向：切一个小栅格，看进度与产物 ----
        // res_3_3600.tif 是 60×60 像素、0.05° 见方，切完只要几秒。
        var small = Path.Combine(repoRoot, "baseline", "maxzoom_calibration", "res_3_3600.tif");
        if (!File.Exists(small))
        {
            Console.Error.WriteLine($"FAIL  找不到测试栅格：{small}");
            return 1;
        }

        var outDir = Path.Combine(workDir, "tiles");
        var progressSeen = new List<double>();
        var logSeen = new List<string>();

        Console.WriteLine($"切片：{Path.GetFileName(small)} → {outDir}");
        var result = CtbTileRunner.Run(
            home,
            new CtbTileRequest(small, outDir),
            onProgress: p => progressSeen.Add(p),
            onLog: logSeen.Add);

        var tiles = Directory.Exists(outDir)
            ? Directory.GetFiles(outDir, "*.terrain", SearchOption.AllDirectories).Length
            : 0;
        var levels = Directory.Exists(outDir)
            ? Directory.GetDirectories(outDir).Select(Path.GetFileName).OrderBy(x => x).ToArray()
            : [];

        var ok = true;
        ok &= Check("退出码", result.RawExitCode.ToString(), "0", result.RawExitCode == 0);
        ok &= Check("stderr 无 Error:", result.SawError ? result.ErrorLine ?? "有" : "无", "无", !result.SawError);
        ok &= Check("实时进度回报次数", progressSeen.Count.ToString(), ">0", progressSeen.Count > 0);
        ok &= Check("进度终值", progressSeen.Count > 0 ? progressSeen[^1].ToString("P0") : "(无)", "100%",
                    progressSeen.Count > 0 && progressSeen[^1] >= 0.999);
        ok &= Check("瓦片数", tiles.ToString(), ">0", tiles > 0);
        ok &= Check("残留 *.tmp", result.TempFilesLeft.Count.ToString(), "0", result.TempFilesLeft.Count == 0);
        Console.WriteLine($"  层级目录: {string.Join(", ", levels)}");
        Console.WriteLine($"  用时: {result.Elapsed.TotalSeconds:F2} s");

        // ---- 取消：切片中途喊停，验证子进程真的终止 ----
        // PRD 验收 #4 明确要求"切片中途取消后子进程确实终止"。
        // 做法：让同一个 60×60 的小栅格切到很深的层级——按瓦片数而不是按栅格大小
        // 制造一个跑得够久的任务，这样测试不依赖任何仓库外的大文件。
        Console.WriteLine();
        Console.WriteLine("取消测试：切到 z18 制造一个长任务，2 秒后取消");
        var cancelDir = Path.Combine(workDir, "tiles_cancel");
        using var cts = new CancellationTokenSource();
        var cancelSawProgress = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var cancelTask = Task.Run(() => CtbTileRunner.Run(
            home,
            new CtbTileRequest(small, cancelDir, StartZoom: 18, EndZoom: 0),
            onProgress: _ => cancelSawProgress = true,
            onLog: null,
            cancellation: cts.Token));

        // 等它真的开始跑再取消，否则测的是"取消一个还没启动的任务"。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!cancelSawProgress && DateTime.UtcNow < deadline) Thread.Sleep(50);
        cts.Cancel();
        var cancelled = cancelTask.GetAwaiter().GetResult();
        sw.Stop();

        var stillRunning = System.Diagnostics.Process.GetProcessesByName("ctb-tile").Length;
        ok &= Check("取消前已经开始跑", cancelSawProgress ? "是" : "否", "是", cancelSawProgress);
        ok &= Check("取消后响应及时（< 10 s）", $"{sw.Elapsed.TotalSeconds:F1} s", "< 10 s",
                    sw.Elapsed.TotalSeconds < 10);
        ok &= Check("没有残留的 ctb-tile 进程", $"{stillRunning} 个", "0 个", stillRunning == 0);
        ok &= Check("残留 *.tmp 已清理", $"{cancelled.TempFilesLeft.Count} 个", "0 个",
                    cancelled.TempFilesLeft.Count == 0);

        // ---- 反向：喂一个坏输入，验证退出码陷阱确实被抓住 ----
        Console.WriteLine();
        Console.WriteLine("负向测试：故意喂一个非栅格文件，验证「失败却返回 0」能被检出");
        var bogus = Path.Combine(workDir, "bogus.tif");
        File.WriteAllBytes(bogus, [0x00, 0x01, 0x02, 0x03]);

        var badOut = Path.Combine(workDir, "tiles_bad");
        var bad = CtbTileRunner.Run(home, new CtbTileRequest(bogus, badOut));

        Console.WriteLine($"  原始退出码: {bad.RawExitCode}");
        Console.WriteLine($"  检出 Error: {(bad.SawError ? bad.ErrorLine : "未检出（危险！）")}");
        ok &= Check("坏输入被判定为失败", bad.SawError ? "是" : "否", "是", bad.SawError);
        Console.WriteLine("  （这条走的是「打不开数据集」路径，源码里 return 1，退出码本来就非零）");

        // ---- 反向二：这才是真正的陷阱 ----
        // 造一个能正常打开、但切片时会抛 CTBException 的栅格（缺坐标系）。
        // 源码里这条路径打印完 "Error:" 后仍然 return 0，退出码是成功的。
        Console.WriteLine();
        Console.WriteLine("负向测试二：缺坐标系的栅格 —— 验证「抛异常却返回 0」这条真正的陷阱");
        var noCrs = Path.Combine(workDir, "no_crs.tif");
        CreateRasterWithoutCrs(noCrs, repoRoot);

        var trapOut = Path.Combine(workDir, "tiles_trap");
        var trap = CtbTileRunner.Run(home, new CtbTileRequest(noCrs, trapOut));

        Console.WriteLine($"  原始退出码: {trap.RawExitCode}  ← 为 0 即证实陷阱存在");
        Console.WriteLine($"  修正后退出码: {trap.ExitCode}");
        Console.WriteLine($"  检出 Error: {(trap.SawError ? trap.ErrorLine : "未检出（危险！）")}");
        ok &= Check("抛异常的情况也被判定为失败", trap.SawError ? "是" : "否", "是", trap.SawError);
        ok &= Check("陷阱被证实（原始退出码为 0）", trap.RawExitCode.ToString(), "0",
                    trap.RawExitCode == 0);

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  闸门二：ctb-tile 子进程可用且失败可检出" : "FAIL  闸门二");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 闸门三：瓦片网格算术对着基线的 tile_counts.txt 逐层核。
    ///
    /// 这道闸门的意义在于：逐层计数将用来**核对 ctb-tile 自己的输出**。
    /// 如果这里的算式与工具差一行一列，对账就会把好产物判成坏的（或反过来），
    /// 所以它必须先被基线证明是对的，才有资格去证明别人。
    /// </summary>
    public static int TileGridGate(string repoRoot)
    {
        var ok = true;

        // ---- 一、四组标定：公式对参考层级的推算 ----
        Console.WriteLine("参考层级公式（对照 baseline/maxzoom_calibration/calibration_results.txt）");
        (double PixelDeg, int Expected)[] calibration =
        [
            (1.0 / 7200, 15),
            (1.0 / 3600, 14),
            (1.0 / 1800, 13),
            (1.0 / 1200, 12),
        ];
        foreach (var (pixelDeg, expected) in calibration)
        {
            var actual = CtbGrid.ReferenceMaxZoom(pixelDeg);
            ok &= Check($"像元 {pixelDeg * 3600:F1} 角秒", actual.ToString(), expected.ToString(),
                        actual == expected);
        }

        // ---- 二、逐层瓦片数：对照 tile_counts.txt ----
        Console.WriteLine();
        var countsFile = Path.Combine(repoRoot, "baseline", "manifests", "tile_counts.txt");
        if (!File.Exists(countsFile))
        {
            Console.Error.WriteLine($"FAIL  找不到 {countsFile}");
            return 1;
        }

        var expectedCounts = new Dictionary<int, long>();
        long expectedTotal = 0;
        foreach (var line in File.ReadAllLines(countsFile))
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2) continue;
            var key = parts[0].Trim();
            if (!long.TryParse(parts[1].Trim(), out var value)) continue;

            if (key.StartsWith("zoom", StringComparison.Ordinal) &&
                int.TryParse(key.AsSpan(4), out var z))
                expectedCounts[z] = value;
            else if (key == "total")
                expectedTotal = value;
        }

        // 教程 §4.2 的裁剪范围（重庆全域预设）。
        var chongqing = new BBox(105.3, 28.1, 110.2, 32.2);
        var estimate = CtbGrid.Estimate(chongqing, 14);

        Console.WriteLine($"重庆全域 {chongqing}，最深 {estimate.MaxZoom} 级");
        foreach (var (zoom, expected) in expectedCounts.OrderBy(kv => kv.Key))
        {
            var actual = zoom < estimate.PerZoom.Length ? estimate.PerZoom[zoom] : 0;
            ok &= Check($"zoom {zoom,2}", actual.ToString("N0"), expected.ToString("N0"), actual == expected);
        }
        ok &= Check("合计", estimate.Total.ToString("N0"), expectedTotal.ToString("N0"),
                    estimate.Total == expectedTotal);

        Console.WriteLine($"  {estimate.Summary()}");
        Console.WriteLine($"  （教程 §5.4 记的是 223,496，基线实测 223,495——教程多算了一个，疑为把 layer.json 计入）");

        // ---- 三、护栏 ----
        Console.WriteLine();
        var over = CtbGrid.Estimate(new BBox(-180, -90, 180, 90), 12);
        var rejection = CtbGrid.ValidateCost(over);
        ok &= Check("护栏拒绝失控输入", rejection is null ? "未拒绝（危险）" : "已拒绝", "已拒绝",
                    rejection is not null);
        ok &= Check("护栏放过基线本身", CtbGrid.ValidateCost(estimate) is null ? "放过" : "误拒", "放过",
                    CtbGrid.ValidateCost(estimate) is null);
        if (rejection is not null) Console.WriteLine($"  拒绝理由: {rejection}");

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  闸门三：瓦片网格算术与基线一致" : "FAIL  闸门三");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// 造一个 65×65、有 geotransform 但**没有坐标系**的 Float32 栅格。
    /// 它能被 GDAL 正常打开，但 CTB 在算瓦片网格时会抛 CTBException——
    /// 正是那条「打印 Error: 后仍返回 0」的路径。
    /// </summary>
    private static void CreateRasterWithoutCrs(string path, string repoRoot)
    {
        _ = repoRoot;
        GdalRuntime.EnsureReady();
        using var driver = Gdal.GetDriverByName("GTiff")
                           ?? throw new InvalidOperationException("没有 GTiff 驱动");
        using var ds = driver.Create(path, 65, 65, 1, DataType.GDT_Float32, null)
                       ?? throw new InvalidOperationException("建栅格失败");
        ds.SetGeoTransform([105.0, 1.0 / 3600, 0.0, 28.0, 0.0, -1.0 / 3600]);
        // 故意不调 SetProjection：坐标系缺失正是触发点。
        ds.GetRasterBand(1).Fill(500.0, 0.0);
        ds.FlushCache();
    }

    private static bool Check(string label, string actual, string expected, bool pass)
    {
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: 实际 {actual} / 期望 {expected}");
        return pass;
    }
}
