namespace CutMap.Cli;

/// <summary>
/// 仓库根定位：从当前目录（再退到 exe 所在目录）往上找含 <c>cutmap\CutMap.sln</c> 的那一级。
///
/// 以前命令的默认路径有 11 处写死 <c>D:\Map</c>——换台机器、换个检出目录跑 CLI 就得每次传参。
/// 找不到就返回当前目录：宁可让相对路径落在原地，也不要悄悄指向一个不存在的 D:\Map。
/// </summary>
internal static class RepoRoot
{
    public static string Locate()
    {
        // 发布目录自带 CTB，应优先使用随包依赖；从任意工作目录运行也不能
        // 误用另一份开发仓库里的引擎或在当前目录查找不存在的 ctb。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "ctb", "bin", "ctb-tile.exe")))
            return AppContext.BaseDirectory;
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "cutmap", "CutMap.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
