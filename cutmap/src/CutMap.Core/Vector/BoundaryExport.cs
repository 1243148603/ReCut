using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CutMap.Core.Geo;
using OSGeo.OGR;

namespace CutMap.Core.Vector;

/// <summary>
/// 边界矢量的读写（FR-14 的 cutline / FR-15 的随产物分发）。
///
/// **分发只跟随"裁剪是否按行政区划边界"**（PRD FR-07 最后一条）：按行政区划
/// *下载*、但按矩形*裁剪*时不得附带下载区域的边界——那不是裁剪范围，
/// 前端按它裁剪渲染会错位。所以这里只提供"把选中的裁剪边界写出去"，
/// 不提供"把下载区域写出去"。
/// </summary>
public static class BoundaryExport
{
    /// <summary>
    /// 写 gdalwarp 用的 cutline。多个区域直接合成一个 GeometryCollection：
    /// <c>-cutline</c> 把整个数据源当作一个掩膜，多边形之间不必先做 dissolve——
    /// 并集是掩膜本身自然得到的，先 union 一遍只是白花时间。
    /// </summary>
    public static string WriteCutline(IReadOnlyList<BoundaryFeature> features, string path)
    {
        if (features.Count == 0)
            throw new ArgumentException("没有选中任何边界", nameof(features));

        var geometryJson = features.Count == 1
            ? features[0].GeometryJson
            : BuildGeometryCollection(features);

        WriteGeoJson(path, geometryJson, crsName: "urn:ogc:def:crs:OGC:1.3:CRS84");
        return path;
    }

    /// <summary>
    /// 写随产物分发的边界 geojson（FR-15）：外轮廓 + 内部下级界线 + 行政中心标注点。
    /// 前端可直接叠加，画出来的界线与地形边界完全吻合。
    /// </summary>
    /// <param name="selected">本次裁剪用的区域。</param>
    /// <param name="store">用于取下级界线；为 null 时只写外轮廓与中心点。</param>
    public static string WriteProductBoundary(
        IReadOnlyList<BoundaryFeature> selected,
        BoundaryStore? store,
        string path,
        string layerName,
        IReadOnlyList<string>? sourceNotes = null)
    {
        if (selected.Count == 0)
            throw new ArgumentException("没有选中任何边界", nameof(selected));

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"type\": \"FeatureCollection\",\n");
        sb.Append("  \"name\": ").Append(JsonSerializer.Serialize(layerName)).Append(",\n");
        sb.Append("  \"crs\": { \"type\": \"name\", \"properties\": { \"name\": \"urn:ogc:def:crs:OGC:1.3:CRS84\" } },\n");
        sb.Append("  \"features\": [\n");

        var parts = new List<string>();

        // 1. 外轮廓。dissolve 成功就用合并后的，失败就退回原始几何——
        //    退回的结果会带上内部界线，但对渲染无害，比直接失败好。
        var outline = TryDissolve(selected) ?? BuildGeometryCollection(selected);
        parts.Add(Feature(outline, new Dictionary<string, object?>
        {
            ["kind"] = "outline",
            ["name"] = string.Join("+", selected.Select(s => s.Name)),
        }));

        // 2. 内部下级界线：把被选中区域的下级也放进来，形成"界线"效果。
        if (store is not null)
        {
            foreach (var parent in selected)
            {
                if (parent.Code is null) continue;
                foreach (var child in store.DescendantsOf(parent.Code))
                {
                    parts.Add(Feature(child.GeometryJson, new Dictionary<string, object?>
                    {
                        ["kind"] = "sublimit",
                        ["name"] = child.Name,
                        ["gb"] = child.Code,
                        ["parent"] = parent.Name,
                    }));
                }
            }
        }

        // 3. 行政中心标注点（name 字段），前端用来放标签。
        foreach (var feature in selected)
        {
            var centre = TryCentroid(feature.GeometryJson);
            if (centre is null) continue;
            var point = $"{{\"type\":\"Point\",\"coordinates\":[{centre.Value.X},{centre.Value.Y}]}}";
            parts.Add(Feature(point, new Dictionary<string, object?>
            {
                ["kind"] = "label",
                ["name"] = feature.Name,
                ["gb"] = feature.Code,
            }));
        }

        if (sourceNotes is { Count: > 0 })
        {
            parts.Add(Feature("null", new Dictionary<string, object?>
            {
                ["kind"] = "source",
                ["note"] = string.Join("；", sourceNotes),
            }));
        }

        sb.Append("    ").Append(string.Join(",\n    ", parts)).Append('\n');
        sb.Append("  ]\n}\n");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // ---------------------------------------------------------------- 几何

    /// <summary>
    /// 用 OGR 的 SQLite 方言做 ST_Union（合并成一个外轮廓）。
    /// 失败返回 null——GEOS 不可用或几何本身有问题时不该让整个导出失败。
    /// </summary>
    private static string? TryDissolve(IReadOnlyList<BoundaryFeature> features)
    {
        try
        {
            var wkt = BuildGeometryCollection(features);
            using var collection = Ogr.CreateGeometryFromJson(wkt);
            using var union = collection.Union(default);
            return union?.ExportToJson([]);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>几何的重心。取不到返回 null（几何为空或非法）。</summary>
    private static (double X, double Y)? TryCentroid(string geometryJson)
    {
        try
        {
            using var geometry = Ogr.CreateGeometryFromJson(geometryJson);
            using var centroid = geometry.Centroid();
            if (centroid is null || centroid.IsEmpty()) return null;
            return (centroid.GetX(0), centroid.GetY(0));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BuildGeometryCollection(IReadOnlyList<BoundaryFeature> features)
    {
        var sb = new StringBuilder("{\"type\":\"GeometryCollection\",\"geometries\":[");
        for (var i = 0; i < features.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(features[i].GeometryJson);
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// 中文必须原样写出。默认的 <see cref="JsonSerializer"/> 会把非 ASCII 转成
    /// <c>\uXXXX</c>——JSON 上是等价的，但这份文件是给前端直接看/直接改的，
    /// 满屏转义序列既没法读也没法 diff。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Feature(string? geometryJson, Dictionary<string, object?> properties)
    {
        var props = string.Join(",", properties
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{JsonSerializer.Serialize(kv.Key, JsonOptions)}:" +
                          $"{JsonSerializer.Serialize(kv.Value, JsonOptions)}"));
        return $"{{\"type\":\"Feature\",\"properties\":{{{props}}},\"geometry\":{geometryJson ?? "null"}}}";
    }

    /// <summary>写一个只含单个几何的 GeoJSON 文件（cutline 用）。</summary>
    private static void WriteGeoJson(string path, string geometryJson, string crsName)
    {
        var text =
            "{\n" +
            "  \"type\": \"FeatureCollection\",\n" +
            $"  \"crs\": {{ \"type\": \"name\", \"properties\": {{ \"name\": \"{crsName}\" }} }},\n" +
            "  \"features\": [\n" +
            "    { \"type\": \"Feature\", \"properties\": {}, \"geometry\": " + geometryJson + " }\n" +
            "  ]\n" +
            "}\n";
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    /// <summary>把选中的区域合成一个范围，用于覆盖检查与结果预览。</summary>
    public static BBox UnionBounds(IReadOnlyList<BoundaryFeature> features) => new(
        features.Min(f => f.Bounds.West),
        features.Min(f => f.Bounds.South),
        features.Max(f => f.Bounds.East),
        features.Max(f => f.Bounds.North));
}
