namespace CutMap.Core.Store;

/// <summary>
/// 用户数据的落点。
///
/// 放 <c>%APPDATA%\CutMap</c> 而不是 exe 旁边，是因为开发期 exe 在
/// <c>bin\Debug\...\win-x64\</c> 下，重新编译就会把目录清掉——下载源、预设、历史
/// 每改一次代码就没了，根本没法用。放到用户的 AppData 里，开发态与打包态行为一致。
///
/// 代价是"绿色版"不再是纯绿色：换机器不会带走配置。对一个记录"这批数据从哪个源
/// 下的"的工具来说，配置跟着用户走比跟着目录走更合理。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(root, "CutMap");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SourcesFile => Path.Combine(DataDirectory, "sources.json");
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string HistoryFile => Path.Combine(DataDirectory, "history.json");
    public static string UserPresetsFile => Path.Combine(DataDirectory, "presets.json");
}
