using System.Reflection;

namespace CutMap.Core;

/// <summary>产品标识。版本号的**唯一来源是 <c>cutmap/Directory.Build.props</c> 的
/// <c>&lt;Version&gt;</c>**，这里运行时从程序集读回来。</summary>
public static class CutMapInfo
{
    public const string Name = "CutMap";

    /// <summary>
    /// 版本号，如 "0.1.0"。
    ///
    /// **不是常量，是运行时读的。** 曾经这里写死一个字符串、打包脚本再拿正则去抠它，
    /// 而安装器又用程序集版本填注册表——同一个版本号三个地方，迟早对不上。
    /// 现在 MSBuild 从 Directory.Build.props 灌进程序集，这里读回来，只有一处可改。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var version = typeof(CutMapInfo).Assembly.GetName().Version;
        if (version is null) return "0.0.0";
        // 三段就够（0.1.0）；第四段是 MSBuild 补的修订号，对用户没意义。
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
