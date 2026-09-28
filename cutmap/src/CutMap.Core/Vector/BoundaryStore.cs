using CutMap.Core.Geo;
using OSGeo.OGR;

namespace CutMap.Core.Vector;

/// <summary>
/// 行政区划边界库（FR-14）。
///
/// 数据是 EPSG:4490（CGCS2000 经纬度），与 WGS84 DEM 兼容、无 GCJ-02 偏移，
/// 所以可以直接当 cutline 用，不必纠偏（PRD §6 已体检）。
///
/// **数据即文件**：内置数据只是 resources 目录下的三个 geojson，换成更高精度的
/// 数据不用改代码。所以这里没有任何"内置数据"的硬编码，只有文件名约定。
/// </summary>
public sealed class BoundaryStore
{
    /// <summary>文件名约定（PRD FR-14）。放到 <c>resources/</c> 下即可被识别。</summary>
    private static readonly (string FileName, BoundaryLevel Level)[] BuiltInFiles =
    [
        ("中国_省.geojson", BoundaryLevel.省),
        ("中国_市.geojson", BoundaryLevel.市),
        ("中国_县.geojson", BoundaryLevel.县),
    ];

    private readonly List<BoundaryFeature> _features = [];
    private readonly List<string> _missing = [];

    public IReadOnlyList<BoundaryFeature> All => _features;

    /// <summary>没找到的数据文件，用于在界面上如实说明"为什么选择器是空的"。</summary>
    public IReadOnlyList<string> MissingFiles => _missing;

    public bool IsEmpty => _features.Count == 0;

    /// <summary>被滤掉的"境界线"要素数（无编码的非行政要素）。</summary>
    public int SkippedNonAdministrative { get; private set; }

    public static BoundaryStore Load(string resourcesDirectory)
    {
        var store = new BoundaryStore();
        foreach (var (fileName, level) in BuiltInFiles)
        {
            var path = Path.Combine(resourcesDirectory, fileName);
            if (!File.Exists(path))
            {
                store._missing.Add(path);
                continue;
            }
            store.LoadFile(path, level);
        }
        return store;
    }

    /// <summary>
    /// 从 exe 所在目录逐级往上找放着边界数据的 <c>resources\</c>。
    /// 开发期它在仓库根，打包后与 exe 同级——两种情况都覆盖，所以不写死层数。
    /// 找不到时返回一个空库（<see cref="IsEmpty"/> 为真，<see cref="MissingFiles"/> 说明缺什么）。
    /// </summary>
    public static BoundaryStore Locate(string? startDirectory = null)
    {
        var dir = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "resources");
            if (BuiltInFiles.Any(f => File.Exists(Path.Combine(candidate, f.FileName))))
                return Load(candidate);
            dir = dir.Parent;
        }
        // 没找到也返回一个空库而不是 null：调用方要的是"有没有数据、缺哪个文件"，
        // 这两种信息空库都能给，null 只能让每个调用点各写一遍判空。
        var missing = new BoundaryStore();
        foreach (var (fileName, _) in BuiltInFiles)
            missing._missing.Add(Path.Combine("<未找到 resources 目录>", fileName));
        return missing;
    }

    /// <summary>导入外部边界（FR-14 的"数据源可替换"）。层级不明的记为「其他」。</summary>
    public int ImportExternal(string path)
    {
        var before = _features.Count;
        LoadFile(path, BoundaryLevel.其他);
        return _features.Count - before;
    }

    private void LoadFile(string path, BoundaryLevel level)
    {
        GdalRuntime.EnsureReady();

        using var dataset = Ogr.Open(path, 0);
        if (dataset is null) throw new IOException($"打不开边界文件：{path}");

        var layer = dataset.GetLayerByIndex(0);
        if (layer is null) return;

        layer.ResetReading();
        Feature? feature;
        while ((feature = layer.GetNextFeature()) is not null)
        {
            using var _ = feature;

            var name = feature.GetFieldAsString("name");
            var code = feature.GetFieldAsString("gb");
            if (string.IsNullOrWhiteSpace(name)) name = "(未命名)";

            using var geometry = feature.GetGeometryRef();
            if (geometry is null) continue;

            // 境界线（无编码）不是行政区域，选中它去裁剪得不到可解释的结果。
            if (string.IsNullOrWhiteSpace(code))
            {
                SkippedNonAdministrative++;
                continue;
            }

            var envelope = new Envelope();
            geometry.GetEnvelope(envelope);

            _features.Add(new BoundaryFeature(
                name,
                code.Trim(),
                level,
                new BBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY),
                geometry.ExportToJson([])));
        }
    }

    // ---------------------------------------------------------------- 查询

    /// <summary>按名称或代码搜索。空串返回前 <paramref name="limit"/> 条。</summary>
    public IReadOnlyList<BoundaryFeature> Search(string? term, int limit = 200)
    {
        var query = _features.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            query = query.Where(f =>
                f.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (f.Code?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return [.. query.Take(limit)];
    }

    /// <summary>某个代码下的直接下级。</summary>
    public IReadOnlyList<BoundaryFeature> ChildrenOf(string? parentCode) =>
        [.. _features.Where(f => f.ParentCode == parentCode)];

    /// <summary>
    /// 某个区域下的全部后代（子、孙……）。
    ///
    /// FR-15 要的"内部下级界线"是全层的：选一个省，界线应当画出它所有的市与县，
    /// 只取直接下级会漏掉一整层。层级用递推而不是按前缀匹配——前綴匹配会把
    /// "156500" 这种本身不是区域的中间节点也算进来。
    /// </summary>
    public IReadOnlyList<BoundaryFeature> DescendantsOf(string code)
    {
        var result = new List<BoundaryFeature>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { code };
        var frontier = new Queue<string>();
        frontier.Enqueue(code);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in ChildrenOf(current))
            {
                if (child.Code is null || !seen.Add(child.Code)) continue;
                result.Add(child);
                frontier.Enqueue(child.Code);
            }
        }
        return result;
    }

    public BoundaryFeature? ByCode(string code) =>
        _features.FirstOrDefault(f => f.Code == code);

    /// <summary>各级数量，用于在界面上说明数据到底加载上没有。</summary>
    public string Describe()
    {
        if (IsEmpty)
            return MissingFiles.Count > 0
                ? $"未找到边界数据（{MissingFiles.Count} 个文件缺失）。请把省/市/县 geojson 放到 resources 目录。"
                : "边界数据为空。";

        var byLevel = Enum.GetValues<BoundaryLevel>()
            .Select(level => (level, count: _features.Count(f => f.Level == level)))
            .Where(x => x.count > 0)
            .Select(x => $"{x.level} {x.count}");
        var skipped = SkippedNonAdministrative > 0
            ? $"，已滤掉 {SkippedNonAdministrative} 个无编码的「境界线」要素"
            : "";
        return $"共 {_features.Count} 个行政区域（{string.Join(" / ", byLevel)}）{skipped}。";
    }
}
