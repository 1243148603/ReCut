using System.Diagnostics;
using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.Cli;

/// <summary>切片命令。M1 的验收靠它跑：对现成的 chongqing_dem.tif 切出 223,495 个瓦片。</summary>
internal static class SliceCommand
{
    public static int Run(string repoRoot, string input, string? outputDir, int? maxZoom)
    {
        var home = CtbHome.Default(repoRoot);
        if (!home.IsValid(out var why))
        {
            Console.Error.WriteLine($"FAIL  CTB 包不完整：{why}");
            return 1;
        }

        if (!File.Exists(input))
        {
            Console.Error.WriteLine($"FAIL  输入不存在：{input}");
            return 1;
        }

        outputDir ??= Path.Combine(repoRoot, "cutmap", "_gate", "slice");
        maxZoom ??= TryParseArg(Environment.GetEnvironmentVariable("CUTMAP_MAXZOOM"));

        var info = RasterInfo.Read(input);
        Console.WriteLine($"输入：{Path.GetFileName(input)}");
        Console.WriteLine($"  {info.Summary()}");
        Console.WriteLine($"  范围（GDAL 口径）：{info.Extent}");

        var derived = CtbGrid.ReferenceMaxZoom(info.PixelSizeX);
        Console.WriteLine($"  推导最深层级：{derived}（公式 ceil(log2((180/65)/像元度数))）");
        if (maxZoom is { } mz && mz != derived)
            Console.WriteLine($"  实际使用层级：{mz}（显式指定，与推导值不同）");

        Console.WriteLine($"输出：{outputDir}");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();
        var lastShown = -1;
        SliceOutcome outcome;
        try
        {
            outcome = Slicer.Run(
                home,
                new SliceRequest(input, outputDir, maxZoom),
                onProgress: p =>
                {
                    var pct = (int)(p * 100);
                    if (pct / 10 > lastShown / 10)
                    {
                        lastShown = pct;
                        Console.WriteLine($"  {pct,3}%");
                    }
                },
                onLog: line => Console.Error.WriteLine("  " + line));
        }
        catch (Exception ex) when (ex is InvalidOperationException or RasterException)
        {
            Console.Error.WriteLine($"FAIL  {ex.Message}");
            return 1;
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"ctb-tile 用时 {outcome.Elapsed.TotalSeconds:F1} s（含对账共 {sw.Elapsed.TotalSeconds:F1} s）");
        Console.WriteLine($"预期：{outcome.Expected.Summary()}");

        var ok = outcome.Succeeded;
        if (ok)
        {
            Console.WriteLine($"实际：逐层与预期完全一致，合计 {outcome.ActualTotal:N0} 个瓦片");
        }
        else
        {
            Console.Error.WriteLine($"FAIL  {outcome.FailureReason}");
            Console.Error.WriteLine("逐层差异：");
            foreach (var line in outcome.LevelMismatches) Console.Error.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M1：瓦片数与基线一致" : "FAIL  M1");
        return ok ? 0 : 1;
    }

    private static int? TryParseArg(string? s) =>
        int.TryParse(s, out var v) ? v : null;
}
