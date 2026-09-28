using System.Globalization;
using CutMap.Core.Geo;

namespace CutMap.Core.Download;

/// <param name="LatIndex">分幅西南角的纬度整数。</param>
/// <param name="LonLabel">如 "E106" / "W010"。</param>
/// <param name="Name">如 "N29_E106"，即 URL 模板里的 {name}。</param>
public sealed record DemTile(
    int LatIndex,
    int LonIndex,
    string LatLabel,
    string LonLabel,
    BBox Bounds)
{
    public string Name => $"{LatLabel}_{LonLabel}";
    public override string ToString() => Name;
}

/// <summary>
/// 1°×1° DEM 分幅网格（FR-06）。
///
/// Copernicus GLO-30 的分幅以**整度**为界，命名规则是「西南角」：N29_E106 覆盖
/// 29–30°N / 106–107°E。分幅名里的纬度补两位、经度补三位（N05_E006），
/// 这不是装饰——它是 URL 的一部分，少补一位会 404。
/// </summary>
public static class DemTileGrid
{
    /// <summary>
    /// 枚举与 <paramref name="area"/> **真实相交**的分幅。
    ///
    /// 用"相交"而不是"外接矩形取整"是有意的：一个正好压在整度线上的范围
    /// （例如 105.0–110.0）不该把 104–105 那一列也算进来。差一列就是 5 幅、
    /// 约 225 MB 的流量。
    /// </summary>
    public static IReadOnlyList<DemTile> Enumerate(BBox area)
    {
        if (!area.IsValid(out var reason))
            throw new ArgumentException(reason, nameof(area));

        const double epsilon = 1e-9;

        // 四条边要分两种取整，因为"相交"对边界的方向敏感：
        //
        //   西/南边界：它**落在**哪一幅里，那一幅就相交 → floor。
        //     105.3 落在 [105,106] 这一幅里（不是 [106,107]）。
        //   东/北边界：那一幅若从边界**开始**，就只在一个点上接触，不算相交 → ceil-1。
        //     110.0 落在 [109,110] 的末端，[110,111] 只是碰到，不该算。
        //
        // 直觉上想统一用 floor 或统一用 ceil，都会在"整度范围"上错一列/一行——
        // 而 105–110 这种输入恰恰是最常见的手填方式。
        var westIndex = (int)Math.Floor(area.West + epsilon);
        var southIndex = (int)Math.Floor(area.South + epsilon);
        var eastIndex = (int)Math.Ceiling(area.East - epsilon) - 1;
        var northIndex = (int)Math.Ceiling(area.North - epsilon) - 1;

        var tiles = new List<DemTile>();
        for (var lat = southIndex; lat <= northIndex; lat++)
        {
            for (var lon = westIndex; lon <= eastIndex; lon++)
            {
                tiles.Add(new DemTile(lat, lon, LatitudeLabel(lat), LongitudeLabel(lon),
                                      new BBox(lon, lat, lon + 1, lat + 1)));
            }
        }
        return tiles;
    }

    /// <summary>纬度标签，如 29 → "N29"、-5 → "S05"。补两位。</summary>
    public static string LatitudeLabel(int latitude) =>
        (latitude >= 0 ? "N" : "S") +
        Math.Abs(latitude).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>经度标签，如 106 → "E106"、-5 → "W005"。补三位。</summary>
    public static string LongitudeLabel(int longitude) =>
        (longitude >= 0 ? "E" : "W") +
        Math.Abs(longitude).ToString("D3", CultureInfo.InvariantCulture);
}
