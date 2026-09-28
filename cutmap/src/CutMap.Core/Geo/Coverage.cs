namespace CutMap.Core.Geo;

/// <summary>
/// 目标范围能否被输入 DEM 盖住（FR-01）。
///
/// **为什么这件事必须做。** 覆盖不到的后果不是"少一块"，而是 gdalwarp 照样跑完、
/// CTB 照样切片，产出一片 nodata 瓦片——而 CTB 不处理 nodata，那些值会被当成
/// 真实高程编码进去（TerrainTiler 的编码是 <c>(米 + 1000) × 5</c>，nodata 落在
/// 0 m 附近），前端看到的是平坦台地而不是空洞。查出来比让它静默跑完好得多。
///
/// **用哪个范围。** 输入侧用 <see cref="RasterInfo.PixelCentreExtent"/>，
/// 即按"原点存的是像素中心"还原出的真实外框（README §3）。这是产品的实际覆盖，
/// 也是覆盖判断该问的问题。不要换成 <see cref="RasterInfo.Extent"/>——那是 GDAL
/// 的像素外边缘口径，是**算瓦片数**用的，两者差半个像元（约 15 m）。
/// </summary>
public static class Coverage
{
    /// <summary>每度纬度的米数，只用于把差多少度说得直观些。取 WGS84 平均值。</summary>
    private const double MetresPerDegreeLat = 110_574.0;

    public static CoverageResult Check(IReadOnlyList<RasterInfo> inputs, BBox target)
    {
        if (inputs.Count == 0)
            return new CoverageResult(false, null, 0, 0, 0, 0, ["没有输入文件。"]);

        double west = double.MaxValue, south = double.MaxValue;
        double east = double.MinValue, north = double.MinValue;
        foreach (var info in inputs)
        {
            var box = info.PixelCentreExtent;
            west = Math.Min(west, box.West);
            south = Math.Min(south, box.South);
            east = Math.Max(east, box.East);
            north = Math.Max(north, box.North);
        }

        var union = new BBox(west, south, east, north);

        // 只在真的有缺口时才算差值：浮点上"差 1e-15 度"不该被报成缺一条边。
        const double tolerance = 1e-9;
        var westGap = Math.Max(0, union.West - target.West);
        var southGap = Math.Max(0, union.South - target.South);
        var eastGap = Math.Max(0, target.East - union.East);
        var northGap = Math.Max(0, target.North - union.North);

        List<string> problems = [];
        if (union.West > target.West + tolerance)
            problems.Add(Describe("西", westGap, MetresPerDegreeLon(target.South)));
        if (union.South > target.South + tolerance)
            problems.Add(Describe("南", southGap, MetresPerDegreeLat));
        if (union.East < target.East - tolerance)
            problems.Add(Describe("东", eastGap, MetresPerDegreeLon(target.South)));
        if (union.North < target.North - tolerance)
            problems.Add(Describe("北", northGap, MetresPerDegreeLat));

        var covered = problems.Count == 0;
        if (covered)
            problems.Add($"输入覆盖 {union}，目标 {target} 在其内。");

        return new CoverageResult(covered, union, westGap, southGap, eastGap, northGap, problems);
    }

    /// <summary>经度一度对应的米数随纬度收缩，拿目标南边界算个近似值即可——这里只是把"缺多少度"说得直观些。</summary>
    private static double MetresPerDegreeLon(double latitude) =>
        111_320.0 * Math.Cos(latitude * Math.PI / 180.0);

    private static string Describe(string side, double gapDeg, double metresPerDegree)
    {
        var metres = gapDeg * metresPerDegree;
        var metresText = metres >= 1000 ? $"{metres / 1000:F1} km" : $"{metres:F0} m";
        return $"{side}边缺 {gapDeg:F4}°（约 {metresText}）。";
    }
}

/// <param name="Union">输入的并集范围（像素中心口径）。</param>
/// <param name="WestGap">缺口大小（度）。0 表示该边盖住了。</param>
public sealed record CoverageResult(
    bool Covered,
    BBox? Union,
    double WestGap,
    double SouthGap,
    double EastGap,
    double NorthGap,
    IReadOnlyList<string> Problems)
{
    /// <summary>面向用户的说明，可直接放进对话框。</summary>
    public string Describe() => string.Join(" ", Problems);
}
