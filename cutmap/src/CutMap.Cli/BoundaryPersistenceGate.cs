using CutMap.Core.Vector;

namespace CutMap.Cli;

internal static class BoundaryPersistenceGate
{
    public static int Run(string repo)
    {
        var root = Path.Combine(repo, "cutmap", "_gate", "boundary-persistence", Guid.NewGuid().ToString("N"));
        var imports = Path.Combine(root, "imports");
        Directory.CreateDirectory(root);
        var external = Path.Combine(root, "external.geojson");
        File.WriteAllText(external, """
            {"type":"FeatureCollection","features":[{"type":"Feature","properties":{"name":"测试外部边界"},"geometry":{"type":"Polygon","coordinates":[[[105,28],[106,28],[106,29],[105,29],[105,28]]]}}]}
            """);
        var store = BoundaryStore.Load(Path.Combine(root, "empty"), imports);
        var report = store.ImportExternal(external);
        var reloaded = BoundaryStore.Load(Path.Combine(root, "empty"), imports);
        var pass = report.Imported == 1 && reloaded.All.Count == 1 && reloaded.All[0].Code == store.All[0].Code &&
            reloaded.All[0].GeometryJson == store.All[0].GeometryJson && reloaded.All[0].Source == store.All[0].Source;
        Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] 无编码外部多边形导入后保存，重载编码/几何/署名一致");
        var builtIn = BoundaryStore.Load(Path.Combine(repo, "resources"), Path.Combine(root, "no-import"));
        var chongqing = builtIn.All.Count(b => b.Level == BoundaryLevel.县 && b.Code?.StartsWith("15650", StringComparison.Ordinal) == true);
        Console.WriteLine($"重庆区县：{chongqing}");
        pass &= chongqing == 38;
        Console.WriteLine(chongqing == 38 ? "[PASS] DataV 重庆 38 区县齐全" : "[FAIL] 重庆区县数不符");
        return pass ? 0 : 1;
    }
}
