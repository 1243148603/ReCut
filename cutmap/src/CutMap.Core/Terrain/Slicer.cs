using CutMap.Core.Geo;
using CutMap.Core.Tools;

namespace CutMap.Core.Terrain;

public sealed record SliceRequest(
    string InputPath,
    string OutputDirectory,
    /// <summary>最深层级。null = 按栅格像元尺寸推导（与 ctb-tile 省略 -s 的行为一致）。</summary>
    int? MaxZoom = null,
    /// <summary>最粗层级。null = 0。</summary>
    int? MinZoom = null);

public sealed record SliceOutcome(
    bool Succeeded,
    string? FailureReason,
    int MaxZoom,
    TileEstimate Expected,
    long[] ActualPerZoom,
    long ActualTotal,
    TimeSpan Elapsed,
    IReadOnlyList<string> LevelMismatches)
{
    public long Delta => ActualTotal - Expected.Total;
}

/// <summary>
/// 切片编排：推导层级 → 调用 ctb-tile → **与实际瓦片数对账**。
///
/// 对账这一步不是锦上添花。ctb-tile 在切片过程中抛异常时，往 stderr 打一行
/// <c>Error:</c> 然后照样 <c>return 0</c>（CTB 源码 tools/ctb-tile.cpp 的 runTiler）。
/// 退出码是成功的，只有 stderr 和"少了的瓦片"能说明问题。前者抓"明确报错"，
/// 后者抓"没报错但结果不全"——两条都要有。
/// </summary>
public static class Slicer
{
    public static SliceOutcome Run(
        CtbHome home,
        SliceRequest request,
        Action<double>? onProgress = null,
        Action<string>? onLog = null,
        CancellationToken cancellation = default)
    {
        var info = RasterInfo.Read(request.InputPath);

        if (!info.HasProjection)
            throw new RasterException(
                $"输入没有坐标系，CTB 无法切片：{request.InputPath}（实测缺 CRS 时 ctb-tile 会抛异常）");

        if (info.NeedsNormalization)
        {
            var normalized = Path.Combine(Path.GetTempPath(), "cutmap-normalized-" + Guid.NewGuid().ToString("N") + ".tif");
            try
            {
                onLog?.Invoke($"gdalwarp -t_srs EPSG:4326 -r bilinear \"{request.InputPath}\" \"{normalized}\"");
                Mosaic.Normalize(request.InputPath, normalized, cancellation: cancellation);
                var result = Run(home, request with { InputPath = normalized }, onProgress, onLog, cancellation);
                if (result.Succeeded) CropProducts.BindTiles(request.InputPath, request.OutputDirectory);
                return result;
            }
            finally { if (File.Exists(normalized)) File.Delete(normalized); }
        }

        var maxZoom = request.MaxZoom ?? CtbGrid.ReferenceMaxZoom(info.PixelSizeX);
        var minZoom = request.MinZoom ?? 0;

        // 预估必须用 GDAL 口径的 Extent —— 那才是 CTB 会去切的范围。
        // 用半像元外扩的那个会多算一圈瓦片，对账就会误报。
        var estimate = CtbGrid.Estimate(info.Extent, maxZoom);

        // FR-02：代价失控时拒绝执行，不是提醒。
        if (CtbGrid.ValidateCost(estimate) is { } rejection)
            throw new InvalidOperationException(rejection);

        // 显式传 -s/-e，让界面上显示的层级与实际切的是同一个数（由构造保证）。
        CtbTileResult ctbResult;
        if (ChooseShallowSplit(info, maxZoom, minZoom) is { } shallowStart)
        {
            // **浅层单独起进程。** CTB 的 TerrainTiler 不接受 -m（warp 内存无上限），
            // z0/z1 一个瓦片就覆盖整幅栅格，GDAL 会一次申请 width×height×4 字节。
            // 深层跑完后同一进程里再做这次分配，在内存紧张时会失败（实测在 z0 报
            // "cannot allocate 1026499108 bytes" → "Could not read heights from raster"）。
            // 新进程把地址空间和内存压力重置掉，浅层总共只有几十个瓦片，几乎不增加耗时。
            var deepWeight = DeepWeight(estimate, shallowStart + 1, maxZoom, minZoom);
            var deep = CtbTileRunner.Run(
                home,
                new CtbTileRequest(request.InputPath, request.OutputDirectory, maxZoom, shallowStart + 1),
                onProgress is null ? null : p => onProgress(p * deepWeight),
                onLog, cancellation);
            if (!deep.Succeeded) return Failed(deep, maxZoom, estimate);

            // 浅层本身只有个位数到几十个瓦片，2 个线程足够；限制并发可以让
            // 相邻层的大块分配不同时发生。
            var shallow = CtbTileRunner.Run(
                home,
                new CtbTileRequest(request.InputPath, request.OutputDirectory, shallowStart, minZoom,
                                   ThreadCount: Math.Min(2, Environment.ProcessorCount)),
                onProgress is null ? null : p => onProgress(deepWeight + p * (1.0 - deepWeight)),
                onLog, cancellation);
            if (!shallow.Succeeded) return Failed(shallow, maxZoom, estimate);

            ctbResult = new CtbTileResult(
                shallow.RawExitCode < 0 || deep.RawExitCode < 0 ? -1 : Math.Max(deep.RawExitCode, shallow.RawExitCode),
                deep.ExitCode, deep.Elapsed + shallow.Elapsed,
                deep.SawError || shallow.SawError,
                deep.ErrorLine ?? shallow.ErrorLine,
                [.. deep.TempFilesLeft, .. shallow.TempFilesLeft]);
        }
        else
        {
            ctbResult = CtbTileRunner.Run(
                home,
                new CtbTileRequest(request.InputPath, request.OutputDirectory, maxZoom, minZoom),
                onProgress, onLog, cancellation);
            if (!ctbResult.Succeeded) return Failed(ctbResult, maxZoom, estimate);
        }

        var actual = CountByLevel(request.OutputDirectory, maxZoom);
        var mismatches = new List<string>();
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var expected = z < estimate.PerZoom.Length ? estimate.PerZoom[z] : 0;
            if (actual[z] != expected)
                mismatches.Add($"zoom {z}: 实际 {actual[z]:N0} / 预期 {expected:N0}");
        }

        var total = actual.Sum();
        if (mismatches.Count == 0) CropProducts.BindTiles(request.InputPath, request.OutputDirectory);
        return new SliceOutcome(
            mismatches.Count == 0,
            mismatches.Count == 0 ? null : "瓦片数与预期不符：" + string.Join("；", mismatches),
            maxZoom, estimate, actual, total, ctbResult.Elapsed, mismatches);
    }

    /// <summary>
    /// 需要把浅层拆到独立进程时返回浅层起始层级，否则 null。
    ///
    /// 触发条件：栅格按 Float32 解压后 ≥ 512 MB——CTB 在浅层会把整幅源数据读进
    /// 一次 warp。边界取 z8（一个瓦片约覆盖 0.7°，远小于整幅），浅层进程只需
    /// 处理 z8..z0，实测几十个瓦片、几秒钟。
    /// </summary>
    private static int? ChooseShallowSplit(RasterInfo info, int maxZoom, int minZoom)
    {
        var bytes = (long)info.Width * info.Height * 4;
        if (bytes < 512L * 1024 * 1024) return null;
        var split = Math.Min(8, maxZoom - 1);
        return split >= minZoom && split < maxZoom ? split : null;
    }

    /// <summary>深端瓦片占总数的比例，用来把两个子进程的进度拼成一条 0–100%。</summary>
    private static double DeepWeight(TileEstimate estimate, int firstShallow, int maxZoom, int minZoom)
    {
        long deep = 0, total = 0;
        for (var z = minZoom; z <= maxZoom; z++)
        {
            var count = z < estimate.PerZoom.Length ? estimate.PerZoom[z] : 0;
            total += count;
            if (z >= firstShallow) deep += count;
        }
        return total == 0 ? 0.9 : Math.Clamp((double)deep / total, 0.05, 0.95);
    }

    private static SliceOutcome Failed(CtbTileResult result, int maxZoom, TileEstimate estimate)
    {
        var why = result.ErrorLine ?? $"ctb-tile 退出码 {result.RawExitCode}，且 stderr 无输出";
        return new SliceOutcome(false, why, maxZoom, estimate, [], 0, result.Elapsed, []);
    }

    /// <summary>逐层清点实际产出的 .terrain 文件数。索引即层级，长度 maxZoom+1。</summary>
    public static long[] CountByLevel(string tileDirectory, int maxZoom)
    {
        var counts = new long[maxZoom + 1];
        if (!Directory.Exists(tileDirectory)) return counts;

        for (var z = 0; z <= maxZoom; z++)
        {
            var levelDir = Path.Combine(tileDirectory, z.ToString());
            counts[z] = Directory.Exists(levelDir)
                ? Directory.EnumerateFiles(levelDir, "*.terrain", SearchOption.AllDirectories).LongCount()
                : 0;
        }
        return counts;
    }
}
