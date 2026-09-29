using CutMap.Core.Geo;
using CutMap.Core.Vector;
using OSGeo.OGR;

namespace CutMap.Core.Download;

/// <summary>
/// 按行政区划边界推导要下哪些分幅（FR-06）。
///
/// **为什么值得单独做。** 用外接矩形的话，重庆会多下好几幅——它的形状不是方的，
/// 西北、东南都有大片空角。PRD 说"比外接矩形省流量"，省的就是这些。
///
/// 判据是**真实相交**而不是"中心点在不在里面"：分幅是 1°×1° 的方格，
/// 只要边界蹭到一格，那一格就得下，否则裁剪时边上会缺一块。
/// </summary>
public static class BoundaryTiles
{
    public static IReadOnlyList<DemTile> Enumerate(IReadOnlyList<BoundaryFeature> boundaries)
    {
        if (boundaries.Count == 0) throw new ArgumentException("没有选中任何边界", nameof(boundaries));

        // 先按外接矩形取候选，再逐个做真实相交——候选只有几十上百个，
        // 而边界几何动辄几十万顶点，对每一幅都做一次完整相交是可以接受的。
        var candidates = DemTileGrid.Enumerate(BoundaryExport.UnionBounds(boundaries));

        GdalRuntime.EnsureReady();
        using var union = BuildUnion(boundaries);
        if (union is null) return candidates;   // 几何用不了就退回外接矩形，宁可多下

        var hits = new List<DemTile>(candidates.Count);
        foreach (var tile in candidates)
        {
            using var tilePolygon = TilePolygon(tile);
            if (tilePolygon is null) { hits.Add(tile); continue; }
            if (union.Intersects(tilePolygon)) hits.Add(tile);
        }
        return hits;
    }

    /// <summary>把选中的边界并成一个几何。合并失败就退回几何集合——Intersects 对集合同样成立。</summary>
    private static Geometry? BuildUnion(IReadOnlyList<BoundaryFeature> boundaries)
    {
        try
        {
            using var collection = Ogr.CreateGeometryFromJson(
                boundaries.Count == 1
                    ? boundaries[0].GeometryJson
                    : "{\"type\":\"GeometryCollection\",\"geometries\":[" +
                      string.Join(",", boundaries.Select(b => b.GeometryJson)) + "]}");
            if (collection is null) return null;

            // 边界自相交会让 Intersects 结果不可靠，先修一遍（与 cutline 同样的处理）。
            if (collection.IsValid()) return collection.Clone();
            using var repaired = collection.MakeValid([]);
            return repaired is null || repaired.IsEmpty() ? null : repaired.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>一幅分幅对应的矩形（西 南 东 北 四个角点闭合）。</summary>
    private static Geometry? TilePolygon(DemTile tile)
    {
        try
        {
            var west = tile.Bounds.West;
            var south = tile.Bounds.South;
            var east = tile.Bounds.East;
            var north = tile.Bounds.North;
            // 绑定的签名是 CreateGeometryFromWkt(ref string, SpatialReference)，
            // 坐标系传 null——这里只做相交判断，不涉及坐标变换。
            var wkt = $"POLYGON (({west} {south}, {east} {south}, {east} {north}, " +
                      $"{west} {north}, {west} {south}))";
            return Ogr.CreateGeometryFromWkt(ref wkt, null);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
