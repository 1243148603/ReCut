using OSGeo.GDAL;
using OSGeo.OSR;

namespace CutMap.Core.Geo;

/// <summary>读栅格失败。<see cref="Exception.Message"/> 面向用户，可直接显示。</summary>
public sealed class RasterException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// 一个 GeoTIFF 的摘要（FR-01）。
///
/// **关于"范围"的两种口径，这里必须说清楚。** geotransform 的 (gt[0], gt[3]) 是
/// 左上角，但它是「左上角像素的外边缘」还是「左上角像素的中心」，**从文件字节里
/// 看不出来**——只有产出这份数据的人知道：
///
///   * <b>GDAL 自己的约定是外边缘（pixel-edge）。</b> gdalinfo 打印的
///     "Upper Left / Lower Right" 角点就是按 <c>origin + size * pixel</c> 算的，
///     CTB 算瓦片范围时用的也是这个。gdalwarp 写出的文件遵守这个约定：
///     <c>-te 105.3 …</c> 得到的 gt[0] 正好是 105.3。
///
///   * <b>Copernicus GLO-30 的分幅是像素中心（pixel-centre）。</b> 提供方把
///     原点存成了中心，所以 N28_E105.tif 的 gt[0] 是 104.999861（= 105 − 半个
///     像元）而不是 105.0。它照样铺满整度，只是要多做半像元修正。
///
/// 既然字节里分不出来，就**统一用 <see cref="Extent"/>（GDAL 口径）**——它是文件
/// 声明的、gdalwarp 与 CTB 实际会用的范围——并把半个像元当作两种口径的固有
/// 不确定度，交给用它的地方处理（覆盖判断在 <see cref="Coverage"/> 里留了
/// 半个像元的余量）。反过来假设"原点一定是像素中心"曾经造成过误判：
/// gdalwarp 产物被向内缩了半个像元，"把文件裁成它自己"会被覆盖闸门拦下。
/// </summary>
public sealed record RasterInfo(
    string Path,
    int Width,
    int Height,
    double[] GeoTransform,
    string ProjectionWkt,
    DataType DataType,
    double? NoData,
    long FileBytes)
{
    /// <summary>像元宽度（度，取绝对值）。CTB 不重投影时用的就是它。</summary>
    public double PixelSizeX => Math.Abs(SlicingTransform[1]);

    /// <summary>像元高度（度，取绝对值）。</summary>
    public double PixelSizeY => Math.Abs(SlicingTransform[5]);

    public double[] NativeResolution => [Math.Sqrt(GeoTransform[1] * GeoTransform[1] + GeoTransform[4] * GeoTransform[4]),
        Math.Sqrt(GeoTransform[2] * GeoTransform[2] + GeoTransform[5] * GeoTransform[5])];
    public bool NeedsNormalization => GeoTransform[2] != 0 || GeoTransform[4] != 0 ||
        GeoTransform[1] <= 0 || GeoTransform[5] >= 0 || (HasProjection && EpsgCode is not (4326 or 4490));

    private double[]? _slicingTransform;
    private double[] SlicingTransform
    {
        get
        {
            if (!NeedsNormalization) return GeoTransform;
            if (_slicingTransform is not null) return _slicingTransform;
            GdalRuntime.EnsureReady();
            using var ds = Gdal.Open(Path, Access.GA_ReadOnly) ?? throw new RasterException("无法读取 DEM。");
            using var vrt = Gdal.AutoCreateWarpedVRT(ds, null, "EPSG:4326", ResampleAlg.GRA_Bilinear, 0);
            var gt = new double[6];
            vrt.GetGeoTransform(gt);
            return _slicingTransform = gt;
        }
    }

    private double[][]? _footprint;
    public double[][] Wgs84Footprint
    {
        get
        {
            if (_footprint is not null) return _footprint;
            GdalRuntime.EnsureReady();
            using var source = HasProjection ? new SpatialReference(ProjectionWkt) : null;
            using var target = new SpatialReference("");
            target.ImportFromEPSG(4326);
            source?.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            using var transform = source is null ? null : new CoordinateTransformation(source, target);
            var points = new List<double[]>();
            (double X, double Y)[] corners = [(0, 0), (Width, 0), (Width, Height), (0, Height), (0, 0)];
            for (var edge = 0; edge < 4; edge++)
                for (var step = 0; step < 32; step++)
                {
                    var t = step / 32.0;
                    var px = corners[edge].X * (1 - t) + corners[edge + 1].X * t;
                    var py = corners[edge].Y * (1 - t) + corners[edge + 1].Y * t;
                    double[] point = [GeoTransform[0] + px * GeoTransform[1] + py * GeoTransform[2],
                        GeoTransform[3] + px * GeoTransform[4] + py * GeoTransform[5], 0];
                    transform?.TransformPoint(point);
                    if (!double.IsFinite(point[0]) || !double.IsFinite(point[1]) || Math.Abs(point[0]) > 180 || Math.Abs(point[1]) > 90)
                        throw new RasterException("DEM 无法转换为有效 WGS84 范围，请检查坐标系与日期变更线。");
                    points.Add([point[0], point[1]]);
                }
            points.Add(points[0]);
            return _footprint = [.. points];
        }
    }

    /// <summary>CTB 没有坐标系时会直接抛异常（已实测），所以这个必须为真才允许切片。</summary>
    public bool HasProjection => !string.IsNullOrWhiteSpace(ProjectionWkt);

    /// <summary>
    /// GDAL 口径的范围（外边缘）。**这是 CTB 实际会去切的范围**，也是文件声明的
    /// footprint——瓦片数预估、覆盖判断都用它。
    /// </summary>
    public BBox Extent => new(Wgs84Footprint.Min(p => p[0]), Wgs84Footprint.Min(p => p[1]),
        Wgs84Footprint.Max(p => p[0]), Wgs84Footprint.Max(p => p[1]));

    /// <summary>EPSG 代码；非地理/投影坐标系或无法识别时为 null。</summary>
    public int? EpsgCode
    {
        get
        {
            if (!HasProjection) return null;
            using var srs = new SpatialReference(ProjectionWkt);
            srs.AutoIdentifyEPSG();
            var code = srs.GetAuthorityCode(null);
            return int.TryParse(code, out var n) ? n : null;
        }
    }

    public static RasterInfo Read(string path)
    {
        GdalRuntime.EnsureReady();

        if (!File.Exists(path))
            throw new RasterException($"文件不存在：{path}");

        Dataset? ds = null;
        try
        {
            ds = Gdal.OpenEx(path, (uint)Access.GA_ReadOnly, null, null, null);
            if (ds is null)
                throw new RasterException($"打不开栅格：{path}");

            var gt = new double[6];
            ds.GetGeoTransform(gt);

            using var band = ds.GetRasterBand(1);
            var noData = band is null ? null : ReadNoData(band);

            return new RasterInfo(
                path,
                ds.RasterXSize,
                ds.RasterYSize,
                gt,
                ds.GetProjection() ?? "",
                band?.DataType ?? DataType.GDT_Unknown,
                noData,
                new FileInfo(path).Length);
        }
        catch (RasterException) { throw; }
        catch (Exception ex)
        {
            throw new RasterException($"读取栅格失败：{path}（{ex.Message}）", ex);
        }
        finally
        {
            ds?.Dispose();
        }
    }

    private static double? ReadNoData(Band band)
    {
        // 绑定的签名是 GetNoDataValue(out double value, out int hasValue)：
        // 没有 nodata 时 hasValue 为 0，且 value 的值不可信，不能只看数值。
        band.GetNoDataValue(out var value, out var hasValue);
        return hasValue != 0 ? value : null;
    }

    /// <summary>面向用户的单行摘要。</summary>
    public string Summary()
    {
        var crs = HasProjection ? (EpsgCode is { } code ? $"EPSG:{code}" : "有坐标系(未识别)") : "无坐标系";
        var nodata = NoData is { } nd ? nd.ToString("G6") : "无";
        return $"{Width}×{Height}，{crs}，原像元 {NativeResolution[0]:G6}×{NativeResolution[1]:G6}（CRS 单位），切片参考 {PixelSizeX * 3600:F3}″，{DataType}，nodata={nodata}";
    }
}
