using OSGeo.GDAL;

namespace CutMap.Core.Geo;

/// <summary>
/// 多幅 DEM 拼接 + 裁剪（PRD FR-07 矩形模式）。
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

    /// <summary>
    /// 拼接裁剪。输入顺序即 GDAL 的处理顺序——按教程做法显式列出，不用通配符，
    /// 这样调用参数本身就是一份可复现的记录。
    /// </summary>
    /// <param name="onProgress">进度 0–1。GDAL 按处理过的块回调，粒度足够做进度条。</param>
    /// <param name="cancellation">
    /// 取消令牌。GDAL 的进度回调返回 0 会中止 warp 并让 <c>Gdal.Warp</c> 返回 null，
    /// 所以不需要另起线程去干预。取消后输出文件是残缺的——由调用方决定删不删。
    /// </param>
    /// <returns>输出文件路径。</returns>
    public static string Run(
        IReadOnlyList<string> sources,
        string destination,
        BBox target,
        string resampling = DefaultResampling,
        Action<double>? onProgress = null,
        CancellationToken cancellation = default)
    {
        if (sources.Count == 0)
            throw new ArgumentException("至少需要一个输入文件", nameof(sources));
        if (!target.IsValid(out var reason))
            throw new ArgumentException(reason, nameof(target));
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

            string[] argv =
            [
                "-t_srs", "EPSG:4326",
                "-te", .. target.ToTeArguments(),
                "-te_srs", "EPSG:4326",
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
            using var options = new GDALWarpAppOptions(argv);
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
        var src = string.Join(' ', sources.Select(s => $"\"{s}\""));
        return $"gdalwarp -t_srs EPSG:4326 -te {te} -te_srs EPSG:4326 -r {resampling} " +
               $"-co TILED=YES -co COMPRESS=LZW -overwrite " +
               $"--config GDAL_CACHEMAX {CacheMaxMb} {src} \"{destination}\"";
    }
}
