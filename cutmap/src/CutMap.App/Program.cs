using CutMap.App.Forms;
using CutMap.Core.Tools;

namespace CutMap.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // CTB 是自包含分发包（NFR-02），定位方式是"从 exe 往上找 ctb\bin\ctb-tile.exe"，
        // 开发期与打包后都成立。找不到就没必要把窗口开出来——用户在里面做什么都会失败。
        var home = CtbHome.Locate();
        if (home is null)
        {
            Fail("未在程序目录及其上层找到 ctb\\bin\\ctb-tile.exe");
            return;
        }
        if (!home.IsValid(out var why))
        {
            Fail(why);
            return;
        }

        Application.Run(new MainForm(home));

        static void Fail(string reason) => MessageBox.Show(
            $"找不到 CTB 工具包，无法启动。\n\n{reason}\n\n请确认 ctb 目录与程序放在一起。",
            "CutMap 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.Run(new MainForm(home));
    }
}
