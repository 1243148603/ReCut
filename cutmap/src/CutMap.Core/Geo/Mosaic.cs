using OSGeo.GDAL;

namespace CutMap.Core.Geo;

/// <summary>
/// 多幅 DEM 拼接 + 裁剪（PRD FR-07，矩形与矢量两种模式）。
///
/// **一次性把所有输入交给 warp，不要先建 VRT。** 基线实测：VRT 路径
/// Checksum=31298 ≠ 58775，文件大 300,090 字节，差异集中在 1°×1° 接缝上
/// （126,687 个像素差 > 1 m，最大 2134 m），根因是 VRT 会按自己的网格重采样，
/// 接缝两侧的插值取值与"一次性处理多个源"不同。
/// </summary>
public static class Mosaic
{
    /// <summary>教程 §4.3 用的重采样核；连续地形用双线性。</summary>
    public const string DefaultResampling = "bilinear";

    /// <summary>与教程 §4.2 一致：2 GB 缓存，30 个输入逐个处理时避免反复换页。</summary>
    public const int CacheMaxMb = 2048;

    public static string Normalize(string input, string output, Action<double>? progress = null,
                                   CancellationToken cancellation = default) =>
        Warp([input], output, ["-t_srs", "EPSG:4326"], DefaultResampling, progress, cancellation);

    /// <summary>矩形裁剪。输出范围严格等于 <paramref name="target"/>。</summary>
    public static string Run(
        IReadOnlyList<string> sources,
        string destination,
        BBox target,
        string resampling = DefaultResampling,
        Action<double>? onProgress = null,
        CancellationToken cancellation = default)
    {
        if (!target.IsValid(out var reason))
            throw new ArgumentException(reason, nameof(target));
        if (sources.Count == 0)
            throw new ArgumentException("至少需要一个输入文件", nameof(sources));

        string[] extra =
        [
            "-t_srs", "EPSG:4326",
            "-te", .. target.ToTeArguments(),
            "-te_srs", "EPSG:4326",
        ];
        return Warp(sources, destination, extra, resampling, onProgress, cancellation);
    }

    /// <summary>
    /// 矢量裁剪（FR-07 第二种模式）：<c>-cutline</c> + <c>-crop_to_cutline</c>。
    ///
    /// **必须补裙边。** CTB 不处理 nodata：直接掩膜会让边界外成为 nodata，
    /// 而 TerrainTiler 的编码是 <c>(米 + 1000) × 5</c>，nodata 落在 0 m 附近，
    /// 于是那一片会被当成真实高程编进瓦片——前端看到的不是空洞，是一片平齐的
    /// "海平面"台地。所以裁完之后把边界外填成一个平坦值（默认取输入最低高程），
    /// 视觉上就是一圈裙边。
    /// </summary>
    /// <param name="cutlinePath">cutline 的 GeoJSON/OGR 数据源路径。</param>
    /// <param name="fillValue">
    /// 裙边高程。null 表示自动取输入的最低高程。
    /// 传了值就不再动 nodata 标记；自动取值时会把标记清掉，让裙边读作真实高程。
    /// </param>
    public static string RunVector(
        IReadOnlyList<string> sources,
        string destination,
        string cutlinePath,
        double? fillValue = null,
        string resampling = DefaultResampling,
        Action<double>? onProgress = null,
        CancellationToken cancellation = default)
    {
        if (sources.Count == 0)
            throw new ArgumentException("至少需要一个输入文件", nameof(sources));
        if (!File.Exists(cutlinePath))
            throw new FileNotFoundException("cutline 文件不存在", cutlinePath);

        var fill = fillValue ?? LowestElevation(sources);

        // 不传 -te_srs：矢量模式的输出范围由 -crop_to_cutline 从 cutline 的包围盒
        // 推出来，没有 -te 可解释，带上它 GDAL 会打 "Warning 1: -te_srs ignored"。
        string[] extra =
        [
            "-t_srs", "EPSG:4326",
            "-cutline", cutlinePath,
            "-crop_to_cutline",
            // 边界外先标成这个值；下面再把 nodata 标记清掉，让它变成真实高程。
            "-dstnodata", fill.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];

        var result = Warp(sources, destination, extra, resampling, onProgress, cancellation);

        // 自动取裙边值时清掉 nodata 标记：留着的话，虽然数值正确，但下游
        // （包括算法侧读到的那份 tif）会把整圈裙边判为 nodata，与"补齐"的意图相反。
        if (fillValue is null) ClearNoData(result);

        return result;
    }

    /// <summary>扫各输入的第 1 波段取全局最低高程。用统计量而不是逐像素，30 幅 1.3 GB 也就几秒。</summary>
    private static double LowestElevation(IReadOnlyList<string> sources)
    {
        GdalRuntime.EnsureReady();
        var minimum = double.MaxValue;

        foreach (var path in sources)
        {
            using var dataset = Gdal.OpenEx(path, (uint)Access.GA_ReadOnly, null, null, null);
            using var band = dataset?.GetRasterBand(1);
            if (band is null) continue;

            // 绑定的签名是 ComputeRasterMinMax(double[2] minMax, int approxOk)。
            // approxOk=0 表示要精确值而不是抽样估算——裙边高程宁可慢一点也要确定。
            double[] minMax = [0, 0];
            band.ComputeRasterMinMax(minMax, 0);
            if (minMax[0] < minimum) minimum = minMax[0];
        }

        if (minimum == double.MaxValue)
            throw new InvalidOperationException("无法从输入 DEM 取到最低高程，请显式指定裙边高程。");

        return Math.Floor(minimum);
    }

    private static void ClearNoData(string path)
    {
        using var dataset = Gdal.OpenEx(path, (uint)Access.GA_Update, null, null, null);
        using var band = dataset?.GetRasterBand(1);
        band?.DeleteNoDataValue();
        dataset?.FlushCache();
    }

    /// <summary>打开全部输入、拼参数、跑一次 warp。矩形与矢量两种模式共用这段。</summary>
    private static string Warp(
        IReadOnlyList<string> sources,
        string destination,
        string[] extraArguments,
        string resampling,
        Action<double>? onProgress,
        CancellationToken cancellation)
    {
        if (Path.GetDirectoryName(destination) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        GdalRuntime.EnsureReady();
        Gdal.SetConfigOption("GDAL_CACHEMAX", CacheMaxMb.ToString());

        var datasets = new Dataset[sources.Count];
        var opened = new List<Dataset>(sources.Count);
        try
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var ds = Gdal.OpenEx(sources[i], (uint)Access.GA_ReadOnly, null, null, null);
                if (ds is null)
                    throw new IOException($"打不开输入文件：{sources[i]}");
                datasets[i] = ds;
                opened.Add(ds);
            }

            List<string> argv =
            [
                .. extraArguments,
                "-r", resampling,
                "-of", "GTiff",
                "-co", "TILED=YES",
                "-co", "COMPRESS=LZW",
                "-overwrite",
            ];

            // 进度回调返回 0 → GDAL 中止本次 warp。这是绑定的取消手段：
            // 返回非 0 表示"继续"，返回 0 表示"停"。
            // 不显式命名委托类型——SWIG 生成的类型名不在直觉位置，交给编译器推断。
            // SWIG 生成的签名第 5 个参数是 string（回调透传数据），不是 IntPtr。
            using var options = new GDALWarpAppOptions([.. argv]);
            using var result = Gdal.Warp(
                destination, datasets, options,
                (complete, _, _) =>
                {
                    onProgress?.Invoke(complete);
                    return cancellation.IsCancellationRequested ? 0 : 1;
                },
                null);

            // 取消会让 Gdal.Warp 返回 null，这跟"真的失败"要区分开：
            // 调用方需要知道是用户喊停，而不是数据有问题。
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);

            // Gdal.Warp 在失败时返回 null；GDAL 的异常已由 MaxRev.Gdal.Core 注册的
            // 错误处理器抛出，这里兜底防止把静默失败当成成功。
            if (result is null)
                throw new IOException($"gdalwarp 未产出结果：{destination}");
        }
        finally
        {
            foreach (var ds in opened) ds.Dispose();
        }

        return destination;
    }

    /// <summary>命令行等价形式，用于日志与排障（PRD FR-10）。</summary>
    public static string DescribeCommandLine(
        IReadOnlyList<string> sources,
        string destination,
        BBox target,
        string resampling = DefaultResampling)
    {
        var te = string.Join(' ', target.ToTeArguments());
        return $"gdalwarp -t_srs EPSG:4326 -te {te} -te_srs EPSG:4326 -r {resampling} " +
               $"-co TILED=YES -co COMPRESS=LZW -overwrite " +
               $"--config GDAL_CACHEMAX {CacheMaxMb} {Quote(sources)} \"{destination}\"";
    }

    /// <summary>矢量模式的命令行等价形式。</summary>
    public static string DescribeVectorCommandLine(
        IReadOnlyList<string> sources,
        string destination,
        string cutlinePath,
        string resampling = DefaultResampling) =>
        $"gdalwarp -t_srs EPSG:4326 -cutline \"{cutlinePath}\" -crop_to_cutline " +
        $"-dstnodata <输入最低高程> -r {resampling} -co TILED=YES -co COMPRESS=LZW -overwrite " +
        $"--config GDAL_CACHEMAX {CacheMaxMb} {Quote(sources)} \"{destination}\"";

    private static string Quote(IReadOnlyList<string> paths) =>
        string.Join(' ', paths.Select(p => $"\"{p}\""));
}
