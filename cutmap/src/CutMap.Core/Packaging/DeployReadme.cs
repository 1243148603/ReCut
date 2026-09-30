using System.Text;

namespace CutMap.Core.Packaging;

/// <summary>
/// 随产物交付的部署说明（FR-13）。写成文件而不是只放在界面里，因为拿到这份产物的人
/// 往往不是操作软件的人——交付人员把目录给了前端，前端需要知道怎么让它跑起来。
///
/// **内容不是客套话。** 204 教程 §6.1 只说"把 cesium_terrain/ 复制到前端项目"，
/// 照做会导致地形完全不显示；README §6.5 记录了排查全过程。那条必须写进这里，
/// 否则这个坑会被每一个拿到产物的人重新踩一遍。
/// </summary>
public static class DeployReadme
{
    public static string Build(string layerName, int maxZoom, long tileCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{layerName} — Cesium 地形瓦片");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine();
        sb.AppendLine($"瓦片层级：0 – {maxZoom}");
        sb.AppendLine($"瓦片数量：{tileCount:N0} 个 .terrain");
        sb.AppendLine();
        sb.AppendLine("目录结构");
        sb.AppendLine("--------");
        sb.AppendLine("  layer.json          CesiumTerrainProvider 的入口元数据");
        sb.AppendLine("  0/ 1/ … " + maxZoom + "/           {z}/{x}/{y}.terrain");
        sb.AppendLine();
        sb.AppendLine("部署");
        sb.AppendLine("----");
        sb.AppendLine("把本目录整体放到前端项目的静态资源目录下，例如：");
        sb.AppendLine("  <前端项目>/public/terrain/");
        sb.AppendLine();
        sb.AppendLine("加载：");
        sb.AppendLine("  terrain: await Cesium.CesiumTerrainProvider.fromUrl('/terrain', {");
        sb.AppendLine("    requestWaterMask: true,");
        sb.AppendLine("    requestVertexNormals: true,");
        sb.AppendLine("  }),");
        sb.AppendLine();
        sb.AppendLine("版本提示：CesiumJS 1.136 起旧的构造器与 readyPromise 写法已移除，");
        sb.AppendLine("只能用 CesiumTerrainProvider.fromUrl。");
        sb.AppendLine();
        sb.AppendLine("本机预览（先看效果，不用写前端代码）");
        sb.AppendLine("------------------------------------");
        sb.AppendLine("CutMap 源码仓库根目录自带预览页 terrain_viewer.html 与服务器 serve.py");
        sb.AppendLine("（Cesium 已随仓库分发，不需要联网）：");
        sb.AppendLine();
        sb.AppendLine("  python serve.py --tileset <本目录>     # 启动后终端会打印预览地址");
        sb.AppendLine();
        sb.AppendLine("它做了三件关键的事：对 .terrain 声明 Content-Encoding: gzip（见下节）、");
        sb.AppendLine("把本目录挂载到 /__tileset__/（所以瓦片放在哪个盘都行），");
        sb.AppendLine("并用 URL 里的 ?tiles= 指向它。");
        sb.AppendLine();
        sb.AppendLine("预览页的镜头、层级、采样点与自检项**全部从 layer.json 读**，");
        sb.AppendLine("所以换任何区域/瓦片集都不需要改页面；Cesium 随仓库分发，断网也能开。");
        sb.AppendLine("带 ?debug=1 可展开诊断面板（瓦片请求状态、各点高程自检）。");
        sb.AppendLine();
        sb.AppendLine("⚠ 必须让服务器声明 Content-Encoding: gzip（否则地形永远不显示）");
        sb.AppendLine("-------------------------------------------------------------------");
        sb.AppendLine("CTB 输出的 .terrain 是 **gzip 压缩**的。磁盘上 0/1/0.terrain 只有 86 字节，");
        sb.AppendLine("开头是 gzip 魔数 1f 8b，解压后才是完整的 8452 字节。");
        sb.AppendLine();
        sb.AppendLine("Cesium **不在 JS 里解压**，它依赖浏览器完成透明解压——而这只有当响应带");
        sb.AppendLine("Content-Encoding: gzip 时才会发生。缺这个头，浏览器把压缩字节原样交给");
        sb.AppendLine("Cesium，HeightmapTerrainData 构造时抛 RangeError，**所有瓦片加载均告失败**。");
        sb.AppendLine();
        sb.AppendLine("症状极具误导性：");
        sb.AppendLine("  · 访问日志里瓦片是 200（传输没问题，layer.json 也能正常解析）");
        sb.AppendLine("  · 但 globe 永远停在 0 级，不再请求任何子瓦片，屏幕上没有地形");
        sb.AppendLine("  · Cesium 的 errorEvent 只给一个不带 URL 的 RangeError");
        sb.AppendLine();
        sb.AppendLine("各服务器的写法：");
        sb.AppendLine();
        sb.AppendLine("  nginx：");
        sb.AppendLine("    location ~ \\.terrain$ { gzip_static off; add_header Content-Encoding gzip; }");
        sb.AppendLine();
        sb.AppendLine("  Apache：");
        sb.AppendLine("    Header set Content-Encoding gzip");
        sb.AppendLine();
        sb.AppendLine("  Vite / 开发服务器：默认不发这个头，需要自己加中间件。");
        sb.AppendLine();
        sb.AppendLine("  **不要用 python -m http.server**：它不加该头，结果就是上面那个症状。");
        sb.AppendLine();
        sb.AppendLine("验证方法：");
        sb.AppendLine("  curl --compressed -o t.bin <url>   → 8452 字节（正确）");
        sb.AppendLine("  curl -o t.bin <url>                → 86 字节的 gzip 流（说明服务器没解压声明）");
        sb.AppendLine();
        sb.AppendLine("⚠ 不要把瓦片目录提交进 git");
        sb.AppendLine("-----------------------------");
        sb.AppendLine($"本产物有 {tileCount:N0} 个文件。请在前端项目的 .gitignore 里排除：");
        sb.AppendLine();
        sb.AppendLine("  public/terrain/");
        sb.AppendLine();
        sb.AppendLine("数据来源");
        sb.AppendLine("--------");
        sb.AppendLine("  DEM：Copernicus DEM GLO-30（© ESA / European Commission，需注明出处）");
        sb.AppendLine("  切片：cesium-terrain-builder 0.4.1（MIT）");
        sb.AppendLine("  拼接裁剪：GDAL（MIT）");
        sb.AppendLine();
        sb.AppendLine("格式说明");
        sb.AppendLine("--------");
        sb.AppendLine("  heightmap-1.0：高程存为小端 uint16 数组，原始值 = (米 + 1000) × 5，");
        sb.AppendLine("  可表示 −1000 m 到 +12107 m。每块是 65×65 网格 + 1 字节 childTileMask");
        sb.AppendLine("  + 1 字节 waterMask。");
        sb.AppendLine("  TMS 编号：y 轴自下而上（从南极计数），与滑图相反。");
        return sb.ToString();
    }
}
