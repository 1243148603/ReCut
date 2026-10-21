using System.Text.Json;
using CutMap.Core.Store;

namespace CutMap.Core.Terrain;

public sealed record CropProducts(string DemSha256, string? BoundaryPath, string? BoundarySha256)
{
    public static void BindTiles(string dem, string tiles)
    {
        Directory.CreateDirectory(tiles);
        var boundary = Resolve(dem);
        var target = Path.Combine(tiles, "boundary.geojson");
        if (boundary is not null) File.Copy(boundary, target, overwrite: true);
        else if (File.Exists(target)) File.Delete(target);
        AtomicFile.Write(Path.Combine(tiles, "cutmap-product.json"), JsonSerializer.Serialize(
            new CropProducts(AlgoMetadata.Fingerprint(dem), boundary is null ? null : "boundary.geojson",
                boundary is null ? null : AlgoMetadata.Fingerprint(target))));
    }

    public static string? ResolveTiles(string tiles)
    {
        var manifest = Path.Combine(tiles, "cutmap-product.json");
        if (!File.Exists(manifest)) return null;
        var product = JsonSerializer.Deserialize<CropProducts>(File.ReadAllText(manifest));
        if (product?.BoundaryPath is null) return null;
        if (product.BoundaryPath != "boundary.geojson") throw new InvalidDataException("瓦片边界路径无效。");
        var boundary = Path.Combine(tiles, product.BoundaryPath);
        if (!File.Exists(boundary) || AlgoMetadata.Fingerprint(boundary) != product.BoundarySha256)
            throw new InvalidDataException("瓦片边界校验失败。");
        return boundary;
    }

    public static string PathFor(string dem) => Path.ChangeExtension(dem, ".product.json");
    public static void Write(string dem, string? boundary)
    {
        var product = new CropProducts(AlgoMetadata.Fingerprint(dem),
            boundary is null ? null : Path.GetFullPath(boundary),
            boundary is null ? null : AlgoMetadata.Fingerprint(boundary));
        AtomicFile.Write(PathFor(dem), JsonSerializer.Serialize(product));
    }

    public static string? Resolve(string dem)
    {
        var path = PathFor(dem);
        if (!File.Exists(path)) return null;
        var product = JsonSerializer.Deserialize<CropProducts>(File.ReadAllText(path))
            ?? throw new InvalidDataException("裁剪产物清单为空。");
        if (product.DemSha256 != AlgoMetadata.Fingerprint(dem))
            throw new InvalidDataException("DEM 与裁剪产物清单指纹不一致，请重新裁剪或重新确认边界。");
        if (product.BoundaryPath is null) return null;
        if (!File.Exists(product.BoundaryPath) || AlgoMetadata.Fingerprint(product.BoundaryPath) != product.BoundarySha256)
            throw new InvalidDataException("裁剪边界与产物清单不一致。");
        return product.BoundaryPath;
    }
}
