namespace CutMap.Core.Geo;

/// <summary>
/// 目标范围能否被输入 DEM 盖住（FR-01）。
///
/// **为什么这件事必须做。** 覆盖不到的后果不是"少一块"，而是 gdalwarp 照样跑完、
/// CTB 照样切片，产出一片 nodata 瓦片——而 CTB 不处理 nodata，那些值会被当成
/// 真实高程编码进去（TerrainTiler 的编码是 <c>(米 + 1000) × 5</c>，nodata 落在
/// 0 m 附近），前端看到的是平坦台地而不是空洞。查出来比让它静默跑完好得多。
///
/// **用哪个范围，为什么留半个像元的余量。** 输入侧用 <see cref="RasterInfo.Extent"/>
/// ——GDAL 的像素外边缘口径，文件声明的、gdalwarp 与 CTB 实际会用的范围。但
/// geotransform 的原点存的是"外边缘"还是"像素中心"，**从文件字节里看不出来**：
/// gdalwarp 写出的文件是外边缘口径，Copernicus GLO-30 的 COG 是像素中心口径，
/// 两种口径给出的外框最多相差半个像元（README §3）。所以每边留半个像元的余量——
/// 这以内的"缺口"是口径的不确定度，不是真的没数据（gdalwarp 对边缘半个像元会用
/// 最近的样本补齐，不会留下 nodata）。
///
/// 曾经的做法是直接换用"按像素中心还原"的外框（PixelCentreExtent）。它只对
/// GLO-30 的 COG 成立，对 gdalwarp 产物则把文件自己的外框向内缩了半个像元——
/// 于是"把一幅 DEM 裁成它自己的范围"这种最普通的操作被报成
/// "南边缺 0.0001°（约 15 m）。东边缺 0.0001°（约 14 m）"，开始按钮直接禁用
/// （实测：data\outputs\terrain_tif\chongqing_dem.tif 配"重庆全域"预设）。
/// </summary>
public static class Coverage
{
    /// <summary>每度纬度的米数，只用于把差多少度说得直观些。取 WGS84 平均值。</summary>
    private const double MetresPerDegreeLat = 110_574.0;

    public static CoverageResult Check(IReadOnlyList<RasterInfo> inputs, BBox target)
    {
        if (inputs.Count == 0)
            return new CoverageResult(false, null, 0, 0, 0, 0, ["没有输入文件。"]);

        var union = Union(inputs)!.Value;

        // 半个像元的口径余量（+一个小的浮点余量，免得"差 1e-15 度"被报成缺一条边）。
        var toleranceX = inputs.Max(i => i.PixelSizeX) / 2.0 + 1e-9;
        var toleranceY = inputs.Max(i => i.PixelSizeY) / 2.0 + 1e-9;
        var westGap = Math.Max(0, union.West - target.West);
        var southGap = Math.Max(0, union.South - target.South);
        var eastGap = Math.Max(0, target.East - union.East);
        var northGap = Math.Max(0, target.North - union.North);

        List<string> problems = [];
        if (union.West > target.West + toleranceX)
            problems.Add(Describe("西", westGap, MetresPerDegreeLon(target.South)));
        if (union.South > target.South + toleranceY)
            problems.Add(Describe("南", southGap, MetresPerDegreeLat));
        if (union.East < target.East - toleranceX)
            problems.Add(Describe("东", eastGap, MetresPerDegreeLon(target.South)));
        if (union.North < target.North - toleranceY)
            problems.Add(Describe("北", northGap, MetresPerDegreeLat));

        var covered = problems.Count == 0;
        if (covered)
            problems.Add($"输入覆盖 {union}，目标 {target} 在其内。");

        return new CoverageResult(covered, union, westGap, southGap, eastGap, northGap, problems);
    }

    /// <summary>
    /// 输入的并集范围（GDAL 外边缘口径）。覆盖检查与「用输入范围」按钮共用同一份算法，
    /// 免得两处各写一遍、口径悄悄漂移。
    /// </summary>
    public static BBox? Union(IReadOnlyList<RasterInfo> inputs)
    {
        if (inputs.Count == 0) return null;

        double west = double.MaxValue, south = double.MaxValue;
        double east = double.MinValue, north = double.MinValue;
        foreach (var info in inputs)
        {
            var box = info.Extent;
            west = Math.Min(west, box.West);
            south = Math.Min(south, box.South);
            east = Math.Max(east, box.East);
            north = Math.Max(north, box.North);
        }
        return new BBox(west, south, east, north);
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

/// <param name="Union">输入的并集范围（GDAL 像素外边缘口径）。</param>
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
