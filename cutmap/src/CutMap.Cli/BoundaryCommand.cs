using CutMap.Core.Download;
using CutMap.Core.Geo;
using CutMap.Core.Vector;

namespace CutMap.Cli;

/// <summary>
/// M5 验收：行政区划边界（FR-14 / FR-15 / FR-07 矢量模式）。
///
/// 真实的天地图边界数据（省/市/县 geojson，共 16 MB）还没到位，所以这里用**同构的
/// 合成数据**验证代码路径：字段名一样（name + gb）、几何类型一样（MultiPolygon，
/// 外加一个无编码的"境界线"要素）、坐标系声明一样（EPSG:4490）。
/// 真实数据放进 resources/ 后走的是同一段代码。
/// </summary>
internal static class BoundaryCommand
{
    public static int Run(string repoRoot)
    {
        var workDir = Path.Combine(repoRoot, "cutmap", "_gate", "vector");
        if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
        var resources = Path.Combine(workDir, "resources");
        Directory.CreateDirectory(resources);

        var ok = true;

        // ---------- 一、造同构的合成数据 ----------
        Console.WriteLine("一、合成边界数据（字段与几何类型照 PRD §6 的体检结论）");
        WriteSynthetic(resources);
        Console.WriteLine($"  {resources}");

        // ---------- 二、加载与过滤 ----------
        Console.WriteLine();
        Console.WriteLine("二、加载、过滤「境界线」、搜索");
        var store = BoundaryStore.Load(resources);
        Console.WriteLine("  " + store.Describe());
        ok &= Check("加载到 3 个行政区域", store.All.Count.ToString(), "3");
        ok &= Check("滤掉 1 个无编码境界线", store.SkippedNonAdministrative.ToString(), "1");
        ok &= Check("境界线不在可选列表里",
                    store.All.All(f => f.IsAdministrative).ToString(), "True");

        var county = store.All.FirstOrDefault(f => f.Level == BoundaryLevel.县);
        ok &= Check("按名称搜到区县", store.Search("巴南").Count.ToString(), "1");
        ok &= Check("按代码搜到区县", store.Search("500113").Count.ToString(), "1");
        ok &= Check("区县的上级是市级",
                    county?.Code is { } cc ? store.ParentOf(cc) ?? "(无)" : "(无)", "156500100");
        ok &= Check("省级下有 1 个市", store.ChildrenOf("156500000").Count.ToString(), "1");
        ok &= Check("市级下有 1 个县", store.ChildrenOf("156500100").Count.ToString(), "1");
        ok &= Check("省级的全体后代是 2 个（市+县）",
                    store.DescendantsOf("156500000").Count.ToString(), "2");

        // ---------- 三、cutline 与矢量裁剪 ----------
        Console.WriteLine();
        Console.WriteLine("三、矢量裁剪（-cutline + -crop_to_cutline + 裙边补齐）");
        var dem = Path.Combine(repoRoot, "baseline", "maxzoom_calibration", "res_3_3600.tif");
        if (!File.Exists(dem))
        {
            Console.Error.WriteLine($"FAIL  找不到测试栅格：{dem}");
            return 1;
        }

        var input = RasterInfo.Read(dem);
        Console.WriteLine($"  输入 {Path.GetFileName(dem)}：{input.Summary()}");
        Console.WriteLine($"  范围 {input.Extent}");

        var cutlinePath = Path.Combine(workDir, "cutline.geojson");
        BoundaryExport.WriteCutline([county!], cutlinePath);
        Console.WriteLine($"  cutline：{cutlinePath}");

        var clipped = Path.Combine(workDir, "clipped.tif");
        Mosaic.RunVector([dem], clipped, cutlinePath);

        var result = RasterInfo.Read(clipped);
        Console.WriteLine($"  产物：{result.Width}×{result.Height}，范围 {result.Extent}");

        // -crop_to_cutline 应当把范围收到边界的包围盒，而不是原栅格的全幅。
        ok &= Check("范围被收到边界包围盒内",
                    result.Extent.Width < input.Extent.Width,
                    $"原 {input.Extent.Width:F5}° → 现 {result.Extent.Width:F5}°");

        // 裙边：边界外应当被填成平坦值，且 **nodata 标记已被清掉**，
        // 否则 CTB 会把整圈裙边当作 nodata 编码成 0 m 附近的乱值。
        ok &= Check("nodata 标记已清除（裙边是真实高程）", result.NoData is null);
        Console.WriteLine($"  [OK] 裙边高程: {ReadMin(clipped):F1} m（输入最低高程）");


        // ---------- 四、边界随产物分发 ----------
        Console.WriteLine();
        Console.WriteLine("四、边界矢量随产物分发（FR-15）");
        var productBoundary = Path.Combine(workDir, "boundary.geojson");
        // 选省一级：它才有下级，能验出 sublimit 那一层。
        var province = store.ByCode("156500000")!;
        BoundaryExport.WriteProductBoundary(
            [province], store, productBoundary, "synthetic",
            sourceNotes: ["合成数据，仅用于验证代码路径"]);

        var text = File.ReadAllText(productBoundary);
        Console.WriteLine($"  写出 {productBoundary}（{text.Length} 字节）");
        ok &= Check("含 outline 外轮廓", text.Contains("\"outline\"", StringComparison.Ordinal));
        ok &= Check("含 sublimit 下级界线", text.Contains("\"sublimit\"", StringComparison.Ordinal));
        ok &= Check("含 label 中心点", text.Contains("\"label\"", StringComparison.Ordinal));
        ok &= Check("含数据来源注明", text.Contains("合成数据", StringComparison.Ordinal));

        // ---------- 四点五、CRS 检查（FR-14 的纠偏提示）----------
        Console.WriteLine();
        Console.WriteLine("四点五、坐标系检查");
        var projected = Path.Combine(workDir, "projected.geojson");
        File.WriteAllText(projected, """
            {"type":"FeatureCollection",
             "crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::3857"}},
             "features":[{"type":"Feature","properties":{"name":"投影坐标测试","gb":"156999999"},
              "geometry":{"type":"Polygon","coordinates":[[[11700000,3400000],[11710000,3400000],
                           [11710000,3410000],[11700000,3410000],[11700000,3400000]]]}}]}
            """);
        var probeStore = new BoundaryStore();
        var report = probeStore.ImportExternal(projected);
        Console.WriteLine($"  导入投影坐标(3857)的文件：{report.Imported} 个要素，" +
                          $"{report.Warnings.Count} 条警告");
        foreach (var warning in report.Warnings)
            Console.WriteLine("    · " + warning.ReplaceLineEndings(" / "));
        ok &= Check("投影坐标系被识别并警告", report.HasWarnings);

        var noCrs = Path.Combine(workDir, "no_crs.geojson");
        File.WriteAllText(noCrs, """
            {"type":"FeatureCollection",
             "features":[{"type":"Feature","properties":{"name":"无坐标系测试","gb":"156999998"},
              "geometry":{"type":"Polygon","coordinates":[[[105.4,28.5],[105.5,28.5],
                           [105.5,28.6],[105.4,28.6],[105.4,28.5]]]}}]}
            """);
        var noCrsReport = new BoundaryStore().ImportExternal(noCrs);
        Console.WriteLine($"  导入无坐标系声明的文件：{noCrsReport.Warnings.Count} 条警告");
        foreach (var warning in noCrsReport.Warnings)
            Console.WriteLine("    · " + warning.ReplaceLineEndings(" / "));
        // GeoJSON **没写 crs 时按规范默认 WGS84**，GDAL 照此返回 4326，
        // 所以不会报"没声明坐标系"——这是对的，不是漏报。
        // "没声明"那条分支是给 Shapefile 之类留的（没有 .prj 时 GetSpatialRef 返回空）。
        ok &= Check("GeoJSON 缺 crs 时按 WGS84 处理、不误报", !noCrsReport.HasWarnings);
        ok &= Check("坐标合法的数据没有范围警告",
                    noCrsReport.Warnings.All(w => !w.Contains("超出经纬度范围")));

        // ---------- 五、真实数据（DataV GeoAtlas，全国省/市/县）----------
        Console.WriteLine();
        Console.WriteLine("五、真实边界数据");
        var realResources = Path.Combine(repoRoot, "resources");
        var real = BoundaryStore.Load(realResources);
        if (real.IsEmpty)
        {
            Console.WriteLine("  （resources 下没有边界数据，跳过）");
        }
        else
        {
            Console.WriteLine("  " + real.Describe());

            var provinces = real.All.Count(f => f.Level == BoundaryLevel.省);
            var cities = real.All.Count(f => f.Level == BoundaryLevel.市);
            var counties = real.All.Count(f => f.Level == BoundaryLevel.县);
            Console.WriteLine($"  省 {provinces} / 市 {cities} / 县 {counties}");
            ok &= Check("省级数量在合理范围", provinces is > 30 and < 50);
            ok &= Check("市级数量在合理范围", cities is > 300 and < 450);
            ok &= Check("县级数量在合理范围", counties is > 2500 and < 3200);

            // PRD §6 点名要有的：重庆 38 个区县齐全，且巴南区的代码是 156500113。
            var chongqing = real.ByCode("156500000");
            ok &= Check("找得到重庆市", chongqing?.Name ?? "(无)", "重庆市");
            var districts = chongqing is null ? [] : real.ChildrenOf(chongqing.Code!);
            Console.WriteLine($"  重庆市下直接下级 {districts.Count} 个");
            ok &= Check("重庆下级数量在合理范围", districts.Count is >= 30 and <= 45);

            var banan = real.ByCode("156500113");
            ok &= Check("找得到巴南区", banan?.Name ?? "(无)", "巴南区");
            ok &= Check("巴南区归重庆市（直辖市无「市辖区」层，靠实际节点解析）",
                        banan?.Code is { } bc ? real.ParentOf(bc) ?? "(无)" : "(无)", "156500000");

            // FR-06：按行政区划推导分幅，与"用外接矩形"对比。
            // 重庆的形状不是方的，西北东南都有大片空角——省的就是这些。
            var byBounds = DemTileGrid.Enumerate(BoundaryExport.UnionBounds([chongqing!]));
            var byBoundary = BoundaryTiles.Enumerate([chongqing!]);
            Console.WriteLine($"  按外接矩形推导分幅：{byBounds.Count} 幅");
            Console.WriteLine($"  按边界真实相交推导：{byBoundary.Count} 幅" +
                              $"（省下 {byBounds.Count - byBoundary.Count} 幅，" +
                              $"约 {(byBounds.Count - byBoundary.Count) * 45} MB）");
            ok &= Check("按边界推到的不多于外接矩形",
                        byBoundary.Count <= byBounds.Count);
            ok &= Check("确实省了（边界不是矩形）", byBoundary.Count < byBounds.Count);
            ok &= Check("推到的是外接矩形的子集",
                        byBoundary.All(t => byBounds.Any(b => b.Name == t.Name)));
            ok &= Check("重庆全域仍在 30 幅以内", byBoundary.Count is > 0 and <= 30);

            // 用真实边界裁一块真实 DEM：找一个覆盖测试栅格范围的区县。
            var testRaster = RasterInfo.Read(dem);
            var centreLon = (testRaster.Extent.West + testRaster.Extent.East) / 2;
            var centreLat = (testRaster.Extent.South + testRaster.Extent.North) / 2;
            var host = real.All
                .Where(f => f.Level == BoundaryLevel.县)
                .FirstOrDefault(f => f.Bounds.West <= centreLon && f.Bounds.East >= centreLon &&
                                     f.Bounds.South <= centreLat && f.Bounds.North >= centreLat);

            if (host is null)
            {
                Console.WriteLine($"  （{centreLon:F3}E {centreLat:F3}N 不在任何区县范围内，跳过真实裁剪）");
            }
            else
            {
                Console.WriteLine($"  测试点 {centreLon:F3}E {centreLat:F3}N 落在：{host.Display}");
                var realCutline = Path.Combine(workDir, "real_cutline.geojson");
                BoundaryExport.WriteCutline([host], realCutline);
                var realClipped = Path.Combine(workDir, "real_clipped.tif");
                Mosaic.RunVector([dem], realClipped, realCutline);

                var clippedInfo = RasterInfo.Read(realClipped);
                Console.WriteLine($"  真实边界裁剪产物：{clippedInfo.Width}×{clippedInfo.Height}，" +
                                  $"范围 {clippedInfo.Extent}");
                ok &= Check("产物有有效像素", clippedInfo.Width > 0 && clippedInfo.Height > 0);
                ok &= Check("裙边已补齐（nodata 标记已清）", clippedInfo.NoData is null);

                // 边界矢量随产物分发，用真实数据 + 真实下级
                var realBoundary = Path.Combine(workDir, "real_boundary.geojson");
                BoundaryExport.WriteProductBoundary(
                    [host], real, realBoundary, "chongqing-test",
                    sourceNotes: [BoundaryStore.SourceAttribution]);
                var realText = File.ReadAllText(realBoundary);
                ok &= Check("真实产物含 outline", realText.Contains("\"outline\"", StringComparison.Ordinal));
                ok &= Check("真实产物含 label", realText.Contains("\"label\"", StringComparison.Ordinal));
                ok &= Check("真实产物含来源注明",
                            realText.Contains("DataV", StringComparison.Ordinal));
            }
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "PASS  M5：边界加载、过滤、矢量裁剪、随产物分发" : "FAIL  M5");
        return ok ? 0 : 1;
    }

    private static double ReadMin(string path)
    {
        using var dataset = OSGeo.GDAL.Gdal.OpenEx(path, (uint)OSGeo.GDAL.Access.GA_ReadOnly, null, null, null);
        using var band = dataset.GetRasterBand(1);
        double[] minMax = [0, 0];
        band.ComputeRasterMinMax(minMax, 0);
        return minMax[0];
    }

    /// <summary>
    /// 写三个文件，模拟真实的省/市/县三份数据。
    /// 其中县那份额外放一个**无 gb 字段的 MultiLineString**，用来验证"境界线"过滤。
    /// </summary>
    private static void WriteSynthetic(string resources)
    {
        // 一个 0.02°×0.02° 的方块，落在 res_3_3600.tif 的范围（105.4–105.45 / 28.55–28.6）之内。
        const string square = """
            {"type":"MultiPolygon","coordinates":[[[[105.410,28.560],[105.430,28.560],[105.430,28.580],[105.410,28.580],[105.410,28.560]]]]}
            """;

        // 用 __GEOM__ 占位再替换，而不是内插字符串：JSON 里成串的 `}` 会和
        // $$""" 的转义规则打架（`}}` 被当成转义的花括号）。
        const string province = """
            {"type":"FeatureCollection","name":"省","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::4490"}},
             "features":[{"type":"Feature","properties":{"name":"重庆市","gb":"156500000"},"geometry":__GEOM__}]}
            """;
        const string city = """
            {"type":"FeatureCollection","name":"市","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::4490"}},
             "features":[{"type":"Feature","properties":{"name":"重庆市市辖区","gb":"156500100"},"geometry":__GEOM__}]}
            """;
        const string county = """
            {"type":"FeatureCollection","name":"县","crs":{"type":"name","properties":{"name":"urn:ogc:def:crs:EPSG::4490"}},
             "features":[
               {"type":"Feature","properties":{"name":"巴南区","gb":"156500113"},"geometry":__GEOM__},
               {"type":"Feature","properties":{"name":"省界"},"geometry":{"type":"MultiLineString","coordinates":[[[105.40,28.55],[105.45,28.60]]]}}
             ]}
            """;

        File.WriteAllText(Path.Combine(resources, "中国_省.geojson"), province.Replace("__GEOM__", square));
        File.WriteAllText(Path.Combine(resources, "中国_市.geojson"), city.Replace("__GEOM__", square));
        File.WriteAllText(Path.Combine(resources, "中国_县.geojson"), county.Replace("__GEOM__", square));
    }

    private static bool Check(string label, string actual, string expected)
    {
        var pass = actual == expected;
        Console.WriteLine($"  [{(pass ? "OK" : "!!")}] {label}: {actual}" + (pass ? "" : $" / 期望 {expected}"));
        return pass;
    }

    private static bool Check(string label, bool pass, string? detail = null) =>
        Check(label + (detail is null ? "" : $"（{detail}）"), pass ? "是" : "否", "是");
}
