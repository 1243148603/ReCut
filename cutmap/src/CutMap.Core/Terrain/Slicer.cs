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

        var maxZoom = request.MaxZoom ?? CtbGrid.ReferenceMaxZoom(info.PixelSizeX);
        var minZoom = request.MinZoom ?? 0;

        // 预估必须用 GDAL 口径的 Extent —— 那才是 CTB 会去切的范围。
        // 用半像元外扩的那个会多算一圈瓦片，对账就会误报。
        var estimate = CtbGrid.Estimate(info.Extent, maxZoom);

        // FR-02：代价失控时拒绝执行，不是提醒。
        if (CtbGrid.ValidateCost(estimate) is { } rejection)
            throw new InvalidOperationException(rejection);

        // 显式传 -s/-e，让界面上显示的层级与实际切的是同一个数（由构造保证）。
        var ctbResult = CtbTileRunner.Run(
            home,
            new CtbTileRequest(request.InputPath, request.OutputDirectory, maxZoom, minZoom),
            onProgress, onLog, cancellation);

        if (!ctbResult.Succeeded)
        {
            var why = ctbResult.ErrorLine
                      ?? $"ctb-tile 退出码 {ctbResult.RawExitCode}，且 stderr 无输出";
            return new SliceOutcome(false, why, maxZoom, estimate, [], 0, ctbResult.Elapsed, []);
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
        return new SliceOutcome(
            mismatches.Count == 0,
            mismatches.Count == 0 ? null : "瓦片数与预期不符：" + string.Join("；", mismatches),
            maxZoom, estimate, actual, total, ctbResult.Elapsed, mismatches);
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
