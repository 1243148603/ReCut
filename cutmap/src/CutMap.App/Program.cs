using System.Runtime.InteropServices;
using CutMap.App.Forms;
using CutMap.App.Ui;
using CutMap.Core.Download;
using CutMap.Core.Store;
using CutMap.Core.Tools;
using CutMap.Core.Vector;

namespace CutMap.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 2 && args[0] == "--slice-benchmark")
        {
            var rounds = args.Length > 3 ? int.Parse(args[3]) : 3;
            Environment.Exit(Jobs.EndToEndGate.Benchmark(args[1], args[2], rounds));
        }
        if (args.Length > 1 && args[0] == "--e2e-gate")
        {
            Environment.Exit(Jobs.EndToEndGate.Run(args[1]));
        }
        if (args.Length > 1 && args[0] == "--workflow-gate")
        {
            ApplicationConfiguration.Initialize();
            AttachConsole(-1);
            Environment.Exit(Jobs.WorkflowGate.Run(args[1]));
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(
            "操作失败：" + e.Exception.Message, "CutMap", MessageBoxButtons.OK, MessageBoxIcon.Error);
        if (args.Contains("--layout-check", StringComparer.OrdinalIgnoreCase))
        {
            RunLayoutCheck();
            return;
        }
        if (args.Contains("--layout-dump", StringComparer.OrdinalIgnoreCase))
        {
            RunLayoutDump(args);
            return;
        }

        // PerMonitorV2：多显示器、不同缩放比例下切换时布局不糊。
        // ApplicationConfiguration.Initialize() 已按 csproj 设了默认值，
        // 这里显式声明，避免将来有人改了项目属性而没意识到影响。
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
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

        // 只调一次。曾经这里并排写了两行 Application.Run——关掉主窗口之后
        // 会再开一个，看起来像"关不掉"。
        using var main = new MainForm(home);
        Application.Run(main);

        static void Fail(string reason) => MessageBox.Show(
            $"找不到 CTB 工具包，无法启动。\n\n{reason}\n\n请确认 ctb 目录与程序放在一起。",
            "CutMap 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// 排错用：把指定窗体排版后的控件树与一张渲染图写到临时目录。
    ///
    /// **为什么不往控制台打**：这个 exe 是 WinExe，`AttachConsole` 在
    /// mintty / 重定向下拿不到句柄，输出会静默消失（实测：进程挂住、文件为空）。
    /// 写文件不依赖控制台，渲染图还能直接看"到底长什么样"。
    /// </summary>
    private static void RunLayoutDump(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var rest = args.SkipWhile(a => !a.Equals("--layout-dump", StringComparison.OrdinalIgnoreCase))
                       .Skip(1).ToArray();
        var which = rest.FirstOrDefault() ?? "pack";
        // 可选参数：拼接窗口的预填输入（复现"覆盖检查说什么"）；
        // 打包窗口的三个路径——瓦片目录 / GeoTIFF / 输出目录（复现"按钮为什么点不亮"）。
        var prefill = rest.Skip(1).FirstOrDefault();
        var second = rest.Skip(2).FirstOrDefault();
        var third = rest.Skip(3).FirstOrDefault();

        using var target = which switch
        {
            "pack" => (Form)new PackageForm(prefill, second, third),
            "choose" => new TaskChooserForm(),
            // --outside：把目标挪到输入之外，验证"真缺口仍然被拦住"（负向）。
            "pipeline" => new PipelineForm(),
            "mosaic" => new MosaicForm(prefill is null ? null : [prefill], null, null,
                args.Contains("--outside", StringComparer.OrdinalIgnoreCase)
                    ? new TaskPreset { Id = "outside", Name = "负向", Bounds = [106.3, 29.1, 111.2, 33.2] }
                    : null),
            "slice" => new SliceForm(),
            "download" => new DownloadForm(),
            "boundary" => new BoundaryPickerForm(BoundaryStore.Locate()),
            "history" => new HistoryForm(new HistoryStore()),
            "preset" => new PresetForm(new PresetStore()),
            "main" => new MainForm(CtbHome.Locate() ?? throw new InvalidOperationException("没找到 ctb 工具包")),
            _ => new PackageForm(),
        };

        // **必须在屏幕外真正 Show 一次。** Theme.Apply 挂在 OnLoad 上，会把字体换成
        // 雅黑并触发 AutoScaleMode.Font 的整窗重缩放——不 Show 就量不到这一步，
        // 量到的是"换字体之前"的假数据（第一次 dump 就栽在这）。DrawToBitmap 也只
        // 画已经显示过的控件树，没 Show 过的窗体渲染出来只有背景。
        var probe = $"probe: 构造后 dpi={target.DeviceDpi} design={target.AutoScaleDimensions} current={target.CurrentAutoScaleDimensions} size={target.ClientSize}\n";

        target.StartPosition = FormStartPosition.Manual;
        target.Location = new Point(-4000, -4000);
        target.ShowInTaskbar = false;
        target.Show();
        Application.DoEvents();

        probe += $"probe: Show 后 dpi={target.DeviceDpi} design={target.AutoScaleDimensions} current={target.CurrentAutoScaleDimensions} size={target.ClientSize}\n";

        var outDir = Path.Combine(Path.GetTempPath(), "cutmap-layout");
        Directory.CreateDirectory(outDir);
        var text = probe + LayoutCheck.Dump(which, target);
        text += $"\n--- Show 之后 ClientSize={target.ClientSize.Width}x{target.ClientSize.Height}\n";
        File.WriteAllText(Path.Combine(outDir, which + ".txt"), text);

        using var bitmap = new Bitmap(target.Width, target.Height);
        target.DrawToBitmap(bitmap, new Rectangle(0, 0, target.Width, target.Height));
        bitmap.Save(Path.Combine(outDir, which + ".png"), System.Drawing.Imaging.ImageFormat.Png);

        target.Close();
        Environment.Exit(0);
    }

    /// <summary>
    /// 布局自检：把每个窗体造出来、强制走一遍排版，报告硬错误。
    ///
    /// 存在的理由：这台机器上截图工具不可用，排版只能靠程序化判定。
    /// 它挡的是"控件被压成 0 尺寸""兄弟重叠""超出父容器"这三类硬错误——
    /// 正是"看起来挤成一团"的成因。**间距是否舒服仍要人看**，它管不了。
    /// </summary>
    private static void RunLayoutCheck()
    {
        AttachConsole(-1);
        // 报告是中文。控制台默认走 GBK 代码页，重定向到文件后就是乱码
        // （实测：报告读不了，等于没输出）。固定成 UTF-8，管它是哪个终端。
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (IOException) { }

        var cases = new List<(string Name, Func<Form> Make)>
        {
            ("新建任务", () => (Form)new TaskChooserForm()),
            ("切片", () => (Form)new SliceForm()),
            ("拼接裁剪", () => (Form)new MosaicForm()),
            ("打包 / 导出", () => (Form)new PackageForm()),
            ("一键生产", () => (Form)new PipelineForm()),
            ("下载 DEM", () => (Form)new DownloadForm()),
            ("下载源管理", () => (Form)new SourceEditorForm(new SourceStore())),
            ("行政区划选择", () => (Form)new BoundaryPickerForm(BoundaryStore.Locate())),
            ("任务历史", () => (Form)new HistoryForm(new HistoryStore())),
            ("预设管理", () => (Form)new PresetForm(new PresetStore())),
            ("任务预设编辑", () => (Form)new TaskPresetEditForm(null)),
            ("下载源编辑", () => (Form)new SourceEditForm(null)),
            ("裁剪预设编辑", () => (Form)new ClipPresetEditForm(null)),
        };

        // 主窗口也查。它曾经因为一句 toolbar.BringToFront() 把停靠顺序弄反：
        // 工具条压住任务列表的表头 43 像素，而自检里没有它，就一直没人报。
        // 需要 ctb 工具包在位才构造得出来，不在就跳过（不把环境问题报成排版问题）。
        var ctbHome = CtbHome.Locate();
        if (ctbHome is not null) cases.Add(("主窗口", () => new MainForm(ctbHome)));

        var problems = new List<LayoutCheck.Problem>();
        foreach (var (name, make) in cases)
        {
            try
            {
                using var form = make();
                problems.AddRange(LayoutCheck.Inspect(name, form));
            }
            catch (Exception ex)
            {
                problems.Add(new LayoutCheck.Problem(name, "(构造)", ex.Message));
            }
        }

        // 先验证检查器本身有效：拿一个**故意做坏**的窗体试它。
        // 一个从不报警的检查等于没有检查。
        using var broken = BuildBrokenForm();
        var canary = LayoutCheck.Inspect("（自检用的坏窗体）", broken);
        if (canary.Count < 2)
        {
            Console.WriteLine($"!! 布局检查器失效：故意做坏的窗体只报了 {canary.Count} 处问题");
            Console.WriteLine(LayoutCheck.Report(canary));
            Environment.ExitCode = 1;
            return;
        }
        Console.WriteLine($"检查器自验：故意做坏的窗体报了 {canary.Count} 处（预期 ≥2）✓");
        Console.WriteLine();

        Console.WriteLine(LayoutCheck.Report(problems));
        Console.WriteLine($"检查了 {cases.Count} 个窗体，{problems.Count} 处问题。");
        Console.WriteLine(problems.Count == 0 ? "PASS" : "FAIL");
        Environment.ExitCode = problems.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// 故意做坏的窗体，用来验证 <see cref="LayoutCheck"/> 真的会报警。
    /// 三处问题分别对应它要抓的三类：尺寸为零、文字截断、超出父容器。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "控件挂到 form 之后由 form 负责释放，这里不需要单独 Dispose。")]
    private static Form BuildBrokenForm()
    {
        var form = new Form { Size = new Size(300, 200) };

        // 1. 尺寸为零
        form.Controls.Add(new Button { Text = "零尺寸", Width = 0, Height = 0 });

        // 2. 文字截断：AutoSize 的勾选框塞进 20 像素宽
        var clipped = new CheckBox { Text = "这段文字明显放不下会被截断", AutoSize = true, Width = 20 };
        form.Controls.Add(clipped);

        // 3. 超出父容器
        form.Controls.Add(new Label { Text = "跑到外面去了", Left = 400, Top = 10, AutoSize = true });

        return form;
    }

    /// <summary>这个 exe 是 WinExe，没有控制台；静默模式下把它接回来好让调用方拿到输出。</summary>
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
