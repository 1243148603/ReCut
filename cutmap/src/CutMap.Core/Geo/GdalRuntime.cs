using MaxRev.Gdal.Core;
using OSGeo.GDAL;

namespace CutMap.Core.Geo;

/// <summary>
/// GDAL 运行时的唯一入口。
///
/// 全项目对 GDAL 的访问都必须先经过 <see cref="EnsureReady"/>：GDAL 的驱动注册与
/// PROJ 数据路径设置是进程级的一次性动作，散落各处调用会出难查的顺序问题。
///
/// 绑定版本钉死在 MaxRev.Gdal.Core 3.13.3.580，与基线（conda geo 的 GDAL 3.13.3）
/// 同版本。基线的 Checksum 58775 是验收锚点，换版本就要重新赌 warp 内核是否一致，
/// 所以升级这个包必须重跑 M0 闸门。
/// </summary>
public static class GdalRuntime
{
    private static readonly object Gate = new();
    private static bool _ready;

    /// <summary>注册全部驱动并定位 GDAL_DATA / PROJ_DATA。可重复调用。</summary>
    public static void EnsureReady()
    {
        if (_ready) return;
        lock (Gate)
        {
            if (_ready) return;
            GdalBase.ConfigureAll();
            _ready = true;
        }
    }

    /// <summary>GDAL 版本号，如 "3.13.3"。用于与基线对版本。</summary>
    public static string ReleaseName()
    {
        EnsureReady();
        return Gdal.VersionInfo("RELEASE_NAME");
    }

    /// <summary>当前 PROJ 数据目录。缺失会导致坐标变换失败。</summary>
    public static string? ProjDataPath()
    {
        EnsureReady();
        return Gdal.GetConfigOption("PROJ_DATA", null);
    }
}
