using CutMap.Core.Geo;
using CutMap.Core.Packaging;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace CutMap.Cli;

internal static class CoordinateGate
{
    public static int Run(string repo)
    {
        GdalRuntime.EnsureReady();
        var root = Path.Combine(repo, "cutmap", "_gate", "coordinates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = 0;
        void Check(string name, bool pass)
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}");
            if (!pass) failures++;
        }
        using var crs = new SpatialReference("");
        crs.ImportFromEPSG(4326);
        crs.ExportToWkt(out var wkt, []);
        var original = Path.Combine(root, "geo.tif");
        using (var driver = Gdal.GetDriverByName("GTiff"))
        using (var ds = driver.Create(original, 60, 60, 1, DataType.GDT_Float32, null))
        {
            ds.SetGeoTransform([105.4, 0.001, 0, 28.6, 0, -0.001]);
            ds.SetProjection(wkt);
            using var band = ds.GetRasterBand(1);
            band.Fill(500, 0);
            band.SetNoDataValue(double.NaN);
        }
        var utm = Path.Combine(root, "utm.tif");
        using (var ds = Gdal.Open(original, Access.GA_ReadOnly))
        using (var opts = new GDALWarpAppOptions(["-t_srs", "EPSG:32648"]))
        using (var result = Gdal.Warp(utm, [ds], opts, null, null)) { }
        var projected = RasterInfo.Read(utm);
        Check("UTM 真实范围转换为经纬度", projected.Extent.West > 105 && projected.Extent.East < 106 && projected.Extent.South > 28 && projected.Extent.North < 29);
        Check("UTM 覆盖检查使用真实面", Coverage.Check([projected], new BBox(105.42, 28.56, 105.44, 28.58)).Covered);
        Check("UTM 参考层级非负且合理", CtbGrid.ReferenceMaxZoom(projected.PixelSizeX) is >= 10 and <= 14);
        var rotation = Path.Combine(root, "rotation.tif");
        File.Copy(original, rotation);
        using (var ds = Gdal.Open(rotation, Access.GA_Update))
            ds.SetGeoTransform([105.4, 0.001, 0.0005, 28.6, 0.0002, -0.001]);
        var rotated = RasterInfo.Read(rotation);
        Check("旋转范围含四角", rotated.Extent.East > 105.48 && rotated.Extent.North > 28.61);
        Check("旋转外框空角拒绝覆盖", !Coverage.Check([rotated], new BBox(105.401, 28.541, 105.405, 28.545)).Covered);
        foreach (var input in new[] { utm, rotation })
        {
            var sliced = Slicer.Run(CtbHome.Default(repo), new SliceRequest(input, Path.Combine(root, Path.GetFileNameWithoutExtension(input)), 8));
            Check($"{Path.GetFileName(input)} 实际规范化切片", sliced.Succeeded);
        }
        var meta = AlgoMetadata.Measure(utm, [original]);
        Check("元数据原单位为米", meta.NativeUnit is "metre" or "meter" && meta.NativeResolution![0] > 10);
        Check("元数据 WGS84 范围真实", meta.BoundsWgs84[0] > 105 && meta.BoundsWgs84[2] < 106);
        meta.WriteBeside(utm);
        AlgoMetadata.Refresh(utm);
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(utm, ".json"))))
            Check("未变 TIFF 保留源清单", json.RootElement.GetProperty("source_files")[0].GetString() == Path.GetFullPath(original));
        using (var ds = Gdal.Open(utm, Access.GA_Update))
        using (var band = ds.GetRasterBand(1)) band.Fill(600, 0);
        var changed = AlgoMetadata.Fingerprint(utm);
        AlgoMetadata.Refresh(utm);
        Check("同名 TIFF 修改后重建元数据", File.ReadAllText(Path.ChangeExtension(utm, ".json")).Contains(changed));
        Check("NaN nodata 可以写入 JSON", AlgoMetadata.Measure(original, [original]).ToJson().Contains("NaN"));
        var boundary = Path.Combine(root, "area.geojson");
        File.WriteAllText(boundary, "{\"type\":\"FeatureCollection\",\"features\":[]}");
        CropProducts.Write(original, boundary);
        var tiles = Path.Combine(root, "geo-tiles");
        Slicer.Run(CtbHome.Default(repo), new SliceRequest(original, tiles, 8));
        Packager.Run(new PackageRequest(root, TilesDirectory: tiles));
        Check("只前端可分发绑定边界", File.Exists(Path.Combine(tiles, "boundary.geojson")));
        CropProducts.Write(original, null);
        CropProducts.BindTiles(original, tiles);
        Packager.Run(new PackageRequest(root, TilesDirectory: tiles));
        Check("矩形重跑不带旧边界", !File.Exists(Path.Combine(tiles, "boundary.geojson")));
        Console.WriteLine($"证据目录：{root}");
        return failures == 0 ? 0 : 1;
    }
}
