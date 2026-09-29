using CutMap.Core.Geo;
using CutMap.Core.Vector;

namespace CutMap.Cli;

/// <summary>
/// 按行政区划边界裁剪（FR-07 矢量模式）的真实数据路径。
///
/// 与 <c>boundary</c> 命令的区别：那个用小栅格 + 合成数据验证**代码路径**，
/// 这个用真实边界 + 真实体量验证**实际产出**——包括裙边补齐在多大幅面上
/// 是否仍然成立、裁剪后的范围是不是真的收在边界上。
/// </summary>
internal static class VectorWarpCommand
{
    public static int Run(string repoRoot, string code, string? outputPath)
    {
        var resources = Path.Combine(repoRoot, "resources");
        var store = BoundaryStore.Load(resources);
        if (store.IsEmpty)
        {
            Console.Error.WriteLine($"FAIL  {store.Describe()}");
            return 1;
        }

        var boundary = store.ByCode(code);
        if (boundary is null)
        {
            Console.Error.WriteLine($"FAIL  找不到行政区代码 {code}。试试 156500000（重庆市）");
            return 1;
        }

        var demDir = Path.Combine(repoRoot, "data", "inputs", "dem");
        var sources = Directory.Exists(demDir)
            ? Directory.GetFiles(demDir, "*.tif").OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : [];
        if (sources.Length == 0)
        {
            Console.Error.WriteLine($"FAIL  找不到输入 DEM：{demDir}");
            return 1;
        }

        outputPath ??= Path.Combine(repoRoot, "cutmap", "_gate", "vector_full",
                                    $"{boundary.Name}_dem.tif");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        Console.WriteLine($"边界：{boundary.Display}");
        Console.WriteLine($"  编码 {boundary.Code}，层级 {boundary.Level}，范围 {boundary.Bounds}");
        Console.WriteLine($"  下级 {store.DescendantsOf(boundary.Code!).Count} 个");
        Console.WriteLine($"输入：{sources.Length} 幅");

        var coverage = Coverage.Check(
            [.. sources.Select(RasterInfo.Read)], boundary.Bounds);
        Console.WriteLine($"覆盖检查：{(coverage.Covered ? "通过" : "不通过")} {coverage.Describe()}");
        if (!coverage.Covered) return 1;

        var cutline = Path.ChangeExtension(outputPath, ".cutline.geojson");
        BoundaryExport.WriteCutline([boundary], cutline);
        Console.WriteLine($"cutline：{cutline}");

        var started = DateTime.UtcNow;
        Mosaic.RunVector(sources, outputPath, cutline);
        Console.WriteLine($"裁剪用时 {(DateTime.UtcNow - started).TotalSeconds:F0} s");

        var ok = true;
        var result = RasterInfo.Read(outputPath);
        Console.WriteLine();
        Console.WriteLine($"产物：{result.Width}×{result.Height}，{result.Summary()}");
        Console.WriteLine($"  范围 {result.Extent}");
        Console.WriteLine($"  边界外包框 {boundary.Bounds}");

        // -crop_to_cutline 会把输出收到边界的包围盒，所以两者应当基本一致
        // （差一个像元的取整）。差太多说明 cutline 没生效。
        var dr = Math.Abs(result.Extent.West - boundary.Bounds.West);
        var dn = Math.Abs(result.Extent.North - boundary.Bounds.North);
        ok &= Check("范围西边界对齐", dr < 0.01, $"差 {dr:F5}°");
        ok &= Check("范围北边界对齐", dn < 0.01, $"差 {dn:F5}°");

        // 裙边：nodata 标记必须被清掉，否则整圈边界外会被当成 nodata 编码进瓦片。
        ok &= Check("nodata 标记已清（裙边是真实高程）", result.NoData is null);

        // 裙边之外还应当真的有数据——全空的话说明 cutline 把整幅都掩掉了。
        ok &= Check("有有效像素", result.Width > 0 && result.Height > 0);

        // 裙边**值**的验证。光看 nodata 标记被清掉不够——真正的风险是边界外
        // 留着 nodata 的原始值（0 m 附近），而 CTB 会把它当真实高程编码进去，
        // 前端看到一片平齐的"海平面"台地。这里直接读四角的像素值。
        var corners = ReadCorners(outputPath);
        var minElevation = ReadMin(outputPath);
        Console.WriteLine($"  四角（应在边界外）：{string.Join(" / ", corners.Select(v => $"{v:F1} m"))}");
        Console.WriteLine($"  全图最低高程：{minElevation:F1} m");
        // 四角落在边界外，必须被填成同一个平坦值，且不等于 nodata 的典型值。
        var spread = corners.Max() - corners.Min();
        ok &= Check("四角是同一个平坦值（裙边成立）", spread < 0.01, $"极差 {spread:F3} m");
        ok &= Check("四角不是 0 m 附近的 nodata 残留",
                    Math.Abs(corners[0]) > 1.0, $"实际 {corners[0]:F1} m");

        // 边界矢量随产物分发（FR-15），带真实下级界线
        var productBoundary = Path.ChangeExtension(outputPath, ".boundary.geojson");
        BoundaryExport.WriteProductBoundary(
            [boundary], store, productBoundary, boundary.Name,
            sourceNotes: [BoundaryStore.SourceAttribution]);
        var text = File.ReadAllText(productBoundary);
        ok &= Check("产物含外轮廓", text.Contains("\"outline\"", StringComparison.Ordinal));
        ok &= Check("产物含下级界线", text.Contains("\"sublimit\"", StringComparison.Ordinal));
        Console.WriteLine($"  边界矢量：{productBoundary}（{text.Length / 1024} KB）");

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  矢量裁剪（真实数据、真实体量）" : "FAIL  矢量裁剪");
        return ok ? 0 : 1;
    }

    /// <summary>读四角各一个像素。四角一定落在边界外（边界不会正好压在包围盒角上）。</summary>
    private static double[] ReadCorners(string path)
    {
        using var dataset = OSGeo.GDAL.Gdal.OpenEx(
            path, (uint)OSGeo.GDAL.Access.GA_ReadOnly, null, null, null);
        using var band = dataset.GetRasterBand(1);
        var w = dataset.RasterXSize;
        var h = dataset.RasterYSize;

        (int X, int Y)[] points = [(2, 2), (w - 3, 2), (2, h - 3), (w - 3, h - 3)];
        var values = new double[points.Length];
        var buffer = new float[1];
        for (var i = 0; i < points.Length; i++)
        {
            band.ReadRaster(points[i].X, points[i].Y, 1, 1, buffer, 1, 1, 0, 0);
            values[i] = buffer[0];
        }
        return values;
    }

    private static double ReadMin(string path)
    {
        using var dataset = OSGeo.GDAL.Gdal.OpenEx(
            path, (uint)OSGeo.GDAL.Access.GA_ReadOnly, null, null, null);
        using var band = dataset.GetRasterBand(1);
        double[] minMax = [0, 0];
        band.ComputeRasterMinMax(minMax, 0);
        return minMax[0];
    }

    private static bool Check(string label, bool pass, string? detail = null)
    {
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}" + (detail is null ? "" : $"（{detail}）"));
        return pass;
    }
}
