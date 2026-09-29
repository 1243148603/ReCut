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

    /// <summary>
    /// 随产物分发时必须带上的数据来源声明（NFR-07 / PRD §6）。
    ///
    /// **实际用的是 DataV.GeoAtlas，不是 PRD §6 写的天地图。** 天地图的行政区划
    /// 服务需要申请 tk 密钥，无法在无人值守脚本里取；DataV 的公开接口免密、
    /// 字段与几何类型兼容。来源不同，署名要求也不同——照抄 PRD 里的天地图
    /// 会是**错误的署名**。改数据源时改这一处，代码与文档就不会各说各话。
    /// </summary>
    public const string SourceAttribution =
        "行政区划数据来源：DataV.GeoAtlas（阿里云 DataV，https://datav.aliyun.com/portal/school/atlas/area_selector）";

    private readonly List<BoundaryFeature> _features = [];
    private readonly List<string> _missing = [];

    /// <summary>code → 实际存在的上级 code。载入后由 ResolveHierarchy 建好。</summary>
    private readonly Dictionary<string, string?> _parentOf = new(StringComparer.Ordinal);

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
            store.LoadFile(path, level, reportProblems: true);
        }
        store.ResolveHierarchy();
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
    public ImportReport ImportExternal(string path)
    {
        var before = _features.Count;
        var warnings = LoadFile(path, BoundaryLevel.其他);
        return new ImportReport(_features.Count - before, warnings);
    }

    /// <summary>
    /// 读一个文件。<paramref name="reportProblems"/> 为真时检查坐标系（只对导入的
    /// 外部数据做——内置数据是体检过的，每次启动都报一遍没有意义）。
    /// </summary>
    /// <returns>面向用户的警告，可直接显示。</returns>
    private List<string> LoadFile(string path, BoundaryLevel level, bool reportProblems = false)
    {
        GdalRuntime.EnsureReady();
        var warnings = new List<string>();

        using var dataset = Ogr.Open(path, 0);
        if (dataset is null) throw new IOException($"打不开边界文件：{path}");

        var layer = dataset.GetLayerByIndex(0);
        if (layer is null) return warnings;

        if (reportProblems) CheckCoordinateSystem(layer, path, warnings);
        var outOfRange = 0;

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

            // 坐标明显不在经纬度范围内，多半是投影坐标（米）而非度。
            // 这种数据直接拿来当 cutline 会裁出完全错误的地方，且不报错。
            if (envelope.MinX < -180 || envelope.MaxX > 180 ||
                envelope.MinY < -90 || envelope.MaxY > 90)
                outOfRange++;

            _features.Add(new BoundaryFeature(
                name,
                code.Trim(),
                level,
                new BBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY),
                geometry.ExportToJson([])));
        }

        if (outOfRange > 0)
        {
            warnings.Add(
                $"有 {outOfRange} 个要素的坐标超出经纬度范围（-180~180 / -90~90），" +
                "看起来是投影坐标（米）而不是经纬度。\n" +
                "直接用来裁剪会得到完全错误的位置，且不会报错——" +
                "请先用 gdalwarp -t_srs EPSG:4326 转成经纬度。");
        }

        return warnings;
    }

    /// <summary>
    /// 检查数据声明的坐标系（FR-14："含 CRS 纠偏提示"）。
    ///
    /// **只能查到"声明层面"的问题。** PRD 提到的 `china/` 那种
    /// ".prj 声明与实际坐标不符"的情况，光看文件是查不出来的——声明说是 WGS84、
    /// 坐标也落在合理范围内，但实际是 GCJ-02 偏移。那种只能靠范围检查兜底
    /// （偏移通常只有几百米，落在范围内，查不出来），所以警告文案里如实说明。
    /// </summary>
    private static void CheckCoordinateSystem(
        OSGeo.OGR.Layer layer, string path, List<string> warnings)
    {
        try
        {
            // 只用图层的 SRS：这个绑定里 DataSource 没有 GetSpatialRef。
            // 图层的 SRS 为空时就是"没声明"，报出来即可。
            using var srs = layer.GetSpatialRef();
            var name = Path.GetFileName(path);

            if (srs is null)
            {
                warnings.Add($"{name} 没有声明坐标系。" +
                             "本程序按经纬度（WGS84/CGCS2000）使用，请自行确认。");
                return;
            }

            var isGeographic = srs.IsGeographic() == 1;
            srs.AutoIdentifyEPSG();
            var epsg = srs.GetAuthorityCode(null);

            if (!isGeographic)
            {
                warnings.Add(
                    $"{name} 声明的是**投影坐标系**（{srs.GetName()}" +
                    (string.IsNullOrEmpty(epsg) ? "" : $"，EPSG:{epsg}") + "）。\n" +
                    "本程序按经纬度使用边界，投影坐标会被当成度来读，" +
                    "结果会偏到完全错误的位置。请先转成 EPSG:4326。");
                return;
            }

            // 地理坐标系但不常见：提示但不阻断——CGCS2000(4490) 与 WGS84(4326)
            // 在本项目的用途上可以互换（PRD §6 已体检），其他地理坐标系未必。
            if (epsg is not ("4326" or "4490"))
            {
                warnings.Add(
                    $"{name} 声明的是 {srs.GetName()}" +
                    (string.IsNullOrEmpty(epsg) ? "" : $"（EPSG:{epsg}）") + "，不是 WGS84/CGCS2000。\n" +
                    "若两者差异较大，边界位置会有偏差。");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"读不出坐标系声明（{ex.Message}），请自行确认数据是经纬度坐标。");
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

    /// <summary>
    /// 解析每个区域的**实际**上级。
    ///
    /// 不能只用编码算术：直辖市没有"市辖区"那一层，区县的算术上级
    /// （如巴南区的 156500100）在数据里不存在。所以按候选往上找，找到第一个
    /// 真实存在的节点为止——这样直辖市、省直辖县这类不规则情形都自然落位，
    /// 不需要逐种情况打补丁。
    /// </summary>
    private void ResolveHierarchy()
    {
        var byCode = new Dictionary<string, BoundaryFeature>(StringComparer.Ordinal);
        foreach (var feature in _features)
            if (feature.Code is { } code) byCode[code] = feature;

        foreach (var feature in _features)
        {
            if (feature.Code is not { } code) continue;

            var parent = feature.CandidateParentCode;
            while (parent is not null && !byCode.ContainsKey(parent))
            {
                // 候选不存在就往再上一级找：县 → 市 → 省。仍然没有就是顶级。
                parent = parent.EndsWith("0000", StringComparison.Ordinal)
                    ? null                                   // 已经是省级，到头了
                    : parent[..5] + "0000";
            }

            _parentOf[code] = parent;
        }
    }

    /// <summary>某个区域的实际上级代码；顶级区域返回 null。</summary>
    public string? ParentOf(string code) => _parentOf.GetValueOrDefault(code);

    /// <summary>某个代码下的直接下级。</summary>
    public IReadOnlyList<BoundaryFeature> ChildrenOf(string? parentCode) =>
        [.. _features.Where(f => f.Code is { } c &&
                                 _parentOf.GetValueOrDefault(c) == parentCode)];

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

/// <summary>导入外部边界的结果（FR-14）。</summary>
/// <param name="Imported">成功导入的要素数。</param>
/// <param name="Warnings">面向用户的坐标系提示，可直接显示。空表示没有发现问题。</param>
public sealed record ImportReport(int Imported, IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;
}
