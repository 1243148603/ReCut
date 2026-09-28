using System.Text.RegularExpressions;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.Core.Terrain;
using OSGeo.GDAL;

namespace CutMap.Cli;

/// <summary>
/// M2 验收：多幅 DEM → 拼接裁剪 → 算法元数据 json。
/// 判据是基线那两个数：Checksum 58775、尺寸 17640×14760，外加 json 与 gdalinfo 逐项一致。
/// </summary>
internal static class MosaicCommand
{
    private const string BaselineChecksum = "58775";
    private const int ExpectedWidth = 17640;
    private const int ExpectedHeight = 14760;

    public static int Run(string repoRoot, string? inputDir, string? outputPath, string? bboxArg)
    {
        inputDir ??= Path.Combine(repoRoot, "data", "inputs", "dem");
        outputPath ??= Path.Combine(repoRoot, "cutmap", "_gate", "warp", "chongqing_dem.tif");
        var target = ParseBBox(bboxArg) ?? BuiltInPresets.Chongqing.Bounds;

        if (!Directory.Exists(inputDir))
        {
            Console.Error.WriteLine($"FAIL  找不到输入目录：{inputDir}");
            return 1;
        }
        if (!target.IsValid(out var reason))
        {
            Console.Error.WriteLine($"FAIL  范围非法：{reason}");
            return 1;
        }

        var sources = Directory.GetFiles(inputDir, "*.tif").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Console.WriteLine($"输入 {sources.Length} 幅，目标范围 {target}（{BuiltInPresets.Chongqing.Name} 预设）");

        var ok = true;

        // ---- FR-01：逐文件摘要 + 覆盖检查 ----
        Console.WriteLine();
        Console.WriteLine("FR-01 输入摘要与覆盖检查");
        var infos = new List<RasterInfo>(sources.Length);
        foreach (var path in sources)
        {
            try
            {
                infos.Add(RasterInfo.Read(path));
            }
            catch (RasterException ex)
            {
                Console.Error.WriteLine($"  [!!] {Path.GetFileName(path)}：{ex.Message}");
                ok = false;
            }
        }

        var sample = infos.Take(3).Select(i => $"  {Path.GetFileName(i.Path)}：{i.Summary()}");
        foreach (var line in sample) Console.WriteLine(line);
        if (infos.Count > 3) Console.WriteLine($"  …其余 {infos.Count - 3} 幅略");

        var coverage = Coverage.Check(infos, target);
        Console.WriteLine($"  [{(coverage.Covered ? "OK" : "!!")}] {coverage.Describe()}");
        ok &= coverage.Covered;
        if (!coverage.Covered) return 1;

        // ---- 覆盖检查的负向验证：把目标挪到输入之外，必须被拦住 ----
        var outside = new BBox(target.East, target.North, target.East + 1, target.North + 1);
        var shouldFail = Coverage.Check(infos, outside);
        var caught = !shouldFail.Covered;
        Console.WriteLine($"  [{(caught ? "OK" : "!!")}] 负向：目标移出输入范围时应报缺" +
                          $"（{shouldFail.Problems.FirstOrDefault()}）");
        ok &= caught;

        // ---- 拼接裁剪 ----
        Console.WriteLine();
        Console.WriteLine("FR-07 拼接裁剪");
        Console.WriteLine("  " + Mosaic.DescribeCommandLine(sources, outputPath, target));
        var started = DateTime.UtcNow;
        try
        {
            Mosaic.Run(sources, outputPath, target);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL  拼接失败：{ex.Message}");
            return 1;
        }
        Console.WriteLine($"  用时 {(DateTime.UtcNow - started).TotalSeconds:F1} s");

        using (var ds = Gdal.OpenEx(outputPath, (uint)Access.GA_ReadOnly, null, null, null))
        {
            var checksum = Checksum(ds);
            ok &= Check("尺寸", $"{ds.RasterXSize}x{ds.RasterYSize}", $"{ExpectedWidth}x{ExpectedHeight}",
                        ds.RasterXSize == ExpectedWidth && ds.RasterYSize == ExpectedHeight);
            ok &= Check("Checksum", checksum ?? "(无)", BaselineChecksum, checksum == BaselineChecksum);
        }

        // ---- FR-17：算法元数据 ----
        Console.WriteLine();
        Console.WriteLine("FR-17 算法元数据 json");
        var metadata = AlgoMetadata.Measure(outputPath, sources);
        var jsonPath = metadata.WriteBeside(outputPath);
        Console.WriteLine($"  写出 {jsonPath}");

        // 逐项对着 gdalinfo 复核——NFR-06 要求"必须与 tif 实测一致"，
        // 所以核对方式就是把 json 与另一次独立读数比。
        var reread = RasterInfo.Read(outputPath);
        var actualBounds = new[] { reread.Extent.West, reread.Extent.South, reread.Extent.East, reread.Extent.North };
        const double tolerance = 1e-9;
        var boundsMatch = metadata.BoundsWgs84.Length == 4 &&
                          metadata.BoundsWgs84.Zip(actualBounds).All(p => Math.Abs(p.First - p.Second) < tolerance);
        ok &= Check("json 范围 vs 重读 tif",
                    string.Join(", ", metadata.BoundsWgs84.Select(v => v.ToString("F6"))),
                    string.Join(", ", actualBounds.Select(v => v.ToString("F6"))),
                    boundsMatch);
        ok &= Check("json 尺寸 vs 重读 tif",
                    string.Join("x", metadata.SizePx), $"{reread.Width}x{reread.Height}",
                    metadata.SizePx[0] == reread.Width && metadata.SizePx[1] == reread.Height);
        ok &= Check("json 分辨率 vs 重读 tif",
                    metadata.Resolution.Degrees[0].ToString("G10"), reread.PixelSizeX.ToString("G10"),
                    Math.Abs(metadata.Resolution.Degrees[0] - reread.PixelSizeX) < 1e-12);
        ok &= Check("json 来源文件数", metadata.SourceFiles.Length.ToString(), sources.Length.ToString(),
                    metadata.SourceFiles.Length == sources.Length);

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M2：拼接裁剪与算法产物与基线一致" : "FAIL  M2");
        return ok ? 0 : 1;
    }

    /// <summary>解析 "西 南 东 北" 四个数。刻意只接受这一种顺序——gdal_translate 的 -projwin 是另一个顺序，混用会静默出错。</summary>
    private static BBox? ParseBBox(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
            throw new ArgumentException("范围需要四个数：西 南 东 北");
        return new BBox(
            double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string? Checksum(Dataset ds)
    {
        using var opts = new GDALInfoOptions(["-checksum"]);
        var m = Regex.Match(Gdal.GDALInfo(ds, opts), @"Checksum=(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool Check(string label, string actual, string expected, bool pass)
    {
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: {actual}" +
                          (actual == expected ? "" : $" / 期望 {expected}"));
        return pass;
    }
}
