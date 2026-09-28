using CutMap.Core.Geo;

namespace CutMap.Core.Terrain;

/// <summary>
/// CTB 的瓦片网格算术：参考层级、瓦片索引、逐层瓦片数。
///
/// 这里的每一条都是照抄 cesium-terrain-builder 自己的算式，而不是"等价地
/// 重写得更漂亮"。理由是这些数字要用来**判定工具自己的输出对不对**，跟工具
/// 差一行一列就会让闸门说谎。两个出处：
///
///   src/Grid.hpp        mInitialResolution = (extent.width / rootTiles) / tileSize
///                       resolution(z)      = mInitialResolution / 2^z
///                       zoomForResolution  = ceil(log2(mInitialResolution / res))
///   src/GDALTiler.cpp   不重投影时 resolution = |gt[1]|（像元尺寸）
/// </summary>
public static class CtbGrid
{
    /// <summary>
    /// 大地（TMS 兼容）剖分的初始分辨率：根节点是 2 个 65×65 瓦片铺满 360°，
    /// 所以 (360/2)/65。这个常数不是调出来的经验值，是 CTB 自己的初值 ——
    /// 它正是 baseline 那四组标定削出 15/14/13/12 的原因。
    /// </summary>
    public const double InitialResolution = (360.0 / 2.0) / 65.0;

    /// <summary>瓦片是 65×65 采样。相邻瓦片有 1 个采样的重叠，那个重叠正是下面计数用闭区间的原因。</summary>
    public const int TileSize = 65;

    /// <summary>
    /// 拒绝提交的瓦片数上限。两百万约是重庆基线（223,495 个 / 1.29 GB / 354 s）
    /// 的 9 倍，也就是约 12 GB、一小时——到这一步操作者几乎肯定是把范围或层级
    /// 填错了，与其跑到四十分钟才发现，不如现在就告诉他。PRD FR-02 要求**拒绝**，不是提醒。
    /// </summary>
    public const long MaxTiles = 2_000_000;

    /// <summary>
    /// 按像元尺寸推算的最深层级——即 ctb-tile 省略 <c>-s</c> 时自己算出来的那个值。
    /// 我们总是显式传 <c>-s</c>，这样界面上显示的、日志里记的、实际切的
    /// 是同一个数（由构造保证，而不是靠巧合）。
    ///
    /// 仅适用于地理坐标系；投影坐标系下此式不成立，须探测工具。
    /// </summary>
    public static int ReferenceMaxZoom(double pixelSizeDeg)
    {
        if (pixelSizeDeg <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelSizeDeg), pixelSizeDeg, "像元尺寸必须为正");
        return (int)Math.Ceiling(Math.Log2(InitialResolution / pixelSizeDeg));
    }

    /// <summary>
    /// 点所在的 (x, y) 瓦片，CTB 的 TMS 网格。
    ///
    /// **y 从南极计数**，不是北极——这是 TMS 约定，也是 .terrain 目录布局用的。
    /// 滑图的 y 是 <c>2^zoom - 1 - y</c>，不要混。
    ///
    /// 算式刻意复现 CTB 的双精度表达式（Grid.hpp 的 crsToPixels 再 pixelsToTile），
    /// 而不是代数化简成更顺眼的写法：当值正好落在瓦片边界上时，floor 的结果
    /// 可能因化简而与 C++ 差一，这里跟工具一致比公式好看更重要。
    /// </summary>
    public static (int X, int Y) TileIndex(double lon, double lat, int zoom)
    {
        var f = Math.Pow(2.0, zoom) / 180.0;
        return ((int)Math.Floor((lon + 180.0) * f), (int)Math.Floor((lat + 90.0) * f));
    }

    /// <summary>
    /// 覆盖 <paramref name="bbox"/> 的闭区间瓦片窗口 (x0, y0, x1, y1)。
    ///
    /// **两端都是闭的**，因为 CTB 就是这么做的，不是因为闭区间更整齐：
    /// GridIterator 取左下与右上两个角，各自 floor 到瓦片，然后 <c>&lt;=</c> 循环。
    /// 按半开区间写（教科书式）会在窗口边正好压在瓦片边界上时，与工具差一行一列。
    /// </summary>
    public static (int X0, int Y0, int X1, int Y1) TileRange(BBox bbox, int zoom)
    {
        var (x0, y0) = TileIndex(bbox.West, bbox.South, zoom);
        var (x1, y1) = TileIndex(bbox.East, bbox.North, zoom);
        return (x0, y0, x1, y1);
    }

    /// <summary>
    /// 某一层级上 <paramref name="bbox"/> 产出的瓦片数。
    ///
    /// 照抄 CTB 的 <c>GridIterator::getSize()</c>：
    /// <code>
    ///   size += (zoomBounds.getWidth() + 1) * (zoomBounds.getHeight() + 1);
    /// </code>
    ///
    /// 那个 <c>+1</c> 是"算得准"而不是"算得近似"的全部原因。它同时意味着：
    /// 窗口边正好压在瓦片边界上时会把边界外那一片也算进来；在世界最东边还会
    /// 多算一列不存在的瓦片——CTB 也照样迭代它、然后产出空。
    /// 这些数字要用来核对工具自己的输出，忠实比整洁值钱。
    /// </summary>
    public static long CountTilesAt(BBox bbox, int zoom)
    {
        var (x0, y0, x1, y1) = TileRange(bbox, zoom);
        return (long)(x1 - x0 + 1) * (y1 - y0 + 1);
    }

    /// <summary>
    /// 0..maxzoom 逐层计数。**每一层都真算，不外推**——计数是精确的、算术是平凡的，
    /// 没有近似的理由。这正是 FR-02 的护栏可以是一个判断、而不是一个启发式的原因。
    /// </summary>
    public static TileEstimate Estimate(BBox bbox, int maxZoom)
    {
        if (maxZoom < 0)
            throw new ArgumentOutOfRangeException(nameof(maxZoom), maxZoom, "层级不能为负");

        var perZoom = new long[maxZoom + 1];
        long total = 0;
        for (var z = 0; z <= maxZoom; z++)
        {
            perZoom[z] = CountTilesAt(bbox, z);
            total += perZoom[z];
        }
        return new TileEstimate(bbox, maxZoom, perZoom, total);
    }

    /// <summary>
    /// 拒绝代价失控的提交。PRD FR-02 要求**拒绝**而不是提醒，
    /// 所以调用方必须把返回值当阻塞条件，不能只用来刷个黄条。
    /// </summary>
    public static string? ValidateCost(TileEstimate estimate) =>
        estimate.Total > MaxTiles
            ? $"瓦片数 {estimate.Total:N0} 超过上限 {MaxTiles:N0}（最深 {estimate.MaxZoom} 级）。请降低最大层级或缩小范围。"
            : null;
}

/// <summary>一次切片在提交之前的代价。</summary>
public sealed record TileEstimate(BBox Bbox, int MaxZoom, long[] PerZoom, long Total)
{
    /// <summary>
    /// 实测：z=14 层有 167,178 个瓦片、995.4 MB，平均 5,954 字节。
    /// 用最深层的均值而不是全局均值来估算体积——最深一层占了 75% 的瓦片数
    /// 和 77% 的字节数；浅层全是 layer.json 和空白水面瓦片，每块几百字节，
    /// 掺进来会把均值拉得远低于实际。
    /// </summary>
    public const long AverageTileBytes = 5954;

    /// <summary>基线：223,495 个瓦片 354 秒（20 核，ctb-tile 默认线程数）。仅用于"约 X 分钟"的提示，不参与任何判定。</summary>
    public const double SecondsPerTile = 354.0 / 223_495.0;

    public long EstimatedBytes => Total * AverageTileBytes;
    public double EstimatedSeconds => Total * SecondsPerTile;

    public string Summary()
    {
        var gb = EstimatedBytes / (1024.0 * 1024 * 1024);
        var minutes = EstimatedSeconds / 60.0;
        return $"最深 {MaxZoom} 级，共 {Total:N0} 个瓦片，约 {gb:F2} GB，预计 {minutes:F1} 分钟";
    }
}
