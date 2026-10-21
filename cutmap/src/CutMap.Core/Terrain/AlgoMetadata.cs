using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CutMap.Core.Geo;
using OSGeo.GDAL;

namespace CutMap.Core.Terrain;

/// <summary>
/// 算法侧产物的伴随元数据（FR-17）。
///
/// **每一条都必须从产物本身量出来，不许照抄输入参数。** 这是 NFR-06 的硬性要求：
/// 元数据 json 描述的是"这个 tif 实际是什么"，而输入参数描述的是"我本来想要什么"，
/// 两者在重采样、裁剪取整之后会不一致——而算法侧拿到 json 是为了不再回来问参数，
/// 一份自述式的 json 比没有 json 更糟，因为它看起来权威却是错的。
/// </summary>
public sealed record AlgoMetadata
{
    [JsonPropertyName("format_version")]
    public required string FormatVersion { get; init; }

    [JsonPropertyName("file")]
    public required string FileName { get; init; }

    [JsonPropertyName("crs")]
    public required CrsInfo Crs { get; init; }

    /// <summary>WGS84 经纬度四至，与 layer.json 的 bounds 同口径（GDAL 像素外边缘）。</summary>
    [JsonPropertyName("bounds_wgs84")]
    public required double[] BoundsWgs84 { get; init; }

    [JsonPropertyName("size_px")]
    public required int[] SizePx { get; init; }

    [JsonPropertyName("resolution")]
    public required ResolutionInfo Resolution { get; init; }

    [JsonPropertyName("dtype")]
    public required string DataType { get; init; }

    [JsonPropertyName("nodata")]
    [JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)]
    public double? NoData { get; init; }

    [JsonPropertyName("raster_sha256")]
    public string? RasterSha256 { get; init; }

    [JsonPropertyName("geotransform")]
    public double[]? GeoTransform { get; init; }

    [JsonPropertyName("resolution_native")]
    public double[]? NativeResolution { get; init; }

    [JsonPropertyName("resolution_native_unit")]
    public string? NativeUnit { get; init; }

    [JsonPropertyName("source_files")]
    public required string[] SourceFiles { get; init; }

    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("software")]
    public required string Software { get; init; }

    public sealed record CrsInfo(
        [property: JsonPropertyName("epsg")] int? Epsg,
        [property: JsonPropertyName("wkt")] string Wkt);

    /// <param name="Degrees">经纬度方向各自的像元尺寸（度）。</param>
    /// <param name="Arcsec">换算成角秒，便于与 GLO-30 这类"1 角秒"产品对照。</param>
    /// <param name="MetresApprox">按范围中心纬度折算的米数。**近似值**：经度方向的米数随纬度变化，这里只给一个便于判断量级的数。</param>
    public sealed record ResolutionInfo(
        [property: JsonPropertyName("degrees")] double[] Degrees,
        [property: JsonPropertyName("arcsec")] double[] Arcsec,
        [property: JsonPropertyName("metres_approx")] double[] MetresApprox);

    public const string CurrentFormatVersion = "1.1";

    public static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    public static string Refresh(string path, Action<string>? log = null)
    {
        AlgoMetadata? old = null;
        var json = Path.ChangeExtension(path, ".json");
        if (File.Exists(json))
        {
            try { old = JsonSerializer.Deserialize<AlgoMetadata>(File.ReadAllText(json)); }
            catch (JsonException ex) { log?.Invoke($"原元数据无效，将重新实测：{ex.Message}"); }
        }
        var measured = Measure(path, [path]);
        if (old is not null && old.RasterSha256 == measured.RasterSha256)
            measured = measured with { SourceFiles = old.SourceFiles, GeneratedAt = old.GeneratedAt };
        else if (old is not null)
            log?.Invoke("TIFF 指纹与原元数据不一致，重新实测；旧源文件溯源不再沿用。");
        return measured.WriteBeside(path);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 中文与文件路径原样写出，不转成 \uXXXX —— 这份 json 是给人看的。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 打开 <paramref name="tifPath"/> 实测各项。**不接收任何"预期值"参数**，
    /// 就是为了让"照抄输入"这件事在签名上就做不到。
    /// </summary>
    public static AlgoMetadata Measure(string tifPath, IReadOnlyList<string> sourceFiles)
    {
        var info = RasterInfo.Read(tifPath);
        var extent = info.Extent;
        var centreLat = (extent.South + extent.North) / 2.0;

        GdalRuntime.EnsureReady();

        return new AlgoMetadata
        {
            FormatVersion = CurrentFormatVersion,
            FileName = Path.GetFileName(tifPath),
            Crs = new CrsInfo(info.EpsgCode, info.ProjectionWkt),
            BoundsWgs84 = [extent.West, extent.South, extent.East, extent.North],
            SizePx = [info.Width, info.Height],
            Resolution = new ResolutionInfo(
                Degrees: [info.PixelSizeX, info.PixelSizeY],
                Arcsec: [info.PixelSizeX * 3600.0, info.PixelSizeY * 3600.0],
                MetresApprox:
                [
                    info.PixelSizeX * 111_320.0 * Math.Cos(centreLat * Math.PI / 180.0),
                    info.PixelSizeY * 110_574.0,
                ]),
            DataType = info.DataType.ToString().Replace("GDT_", ""),
            NoData = info.NoData,
            RasterSha256 = Fingerprint(tifPath),
            GeoTransform = info.GeoTransform,
            NativeResolution = info.NativeResolution,
            NativeUnit = NativeUnitOf(info),
            SourceFiles = [.. sourceFiles.Select(Path.GetFullPath)],
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            Software = $"{CutMapInfo.Name} {CutMapInfo.Version}",
        };
    }

    private static string NativeUnitOf(RasterInfo info)
    {
        using var srs = new OSGeo.OSR.SpatialReference(info.ProjectionWkt);
        return srs.IsProjected() != 0 ? srs.GetLinearUnitsName() : srs.GetAngularUnitsName();
    }

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>与 tif 同名、同目录（FR-17）。返回写出的路径。</summary>
    public string WriteBeside(string tifPath)
    {
        var target = Path.ChangeExtension(tifPath, ".json");
        Store.AtomicFile.Write(target, ToJson());
        return target;
    }
}
