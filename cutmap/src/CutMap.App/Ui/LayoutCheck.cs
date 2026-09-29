using System.Text;

namespace CutMap.App.Ui;

/// <summary>
/// 布局自检：程序化地找出"控件被压扁""兄弟控件重叠""内容被父容器裁掉"。
///
/// **为什么需要它。** 排版问题只有用眼睛看才发现得了，而这台机器上截图工具
/// 不可用。但排版错误里最常见的三类其实是可判定的——尺寸为零、同胞重叠、
/// 超出父容器——它们正是"看起来挤成一团"的成因。把它做成自检，
/// 就能在不看界面的情况下挡住这类回归。
///
/// 不是万能的：**间距是否舒服、对齐是否整齐，仍然要人看**。它只挡硬错误。
/// </summary>
internal static class LayoutCheck
{
    public sealed record Problem(string Form, string Control, string Issue);

    /// <summary>构造窗体、离屏显示一次、强制走一遍布局，返回发现的问题。</summary>
    public static IReadOnlyList<Problem> Inspect(string formName, Form form)
    {
        var problems = new List<Problem>();

        // **必须离屏 Show 一次再量。** 两件事只在 Show 的时候发生：
        //   1. Theme.Apply（在 OnLoad 里）——换字体、套配色；
        //   2. ThemedForm 按 设备DPI/96 缩放整棵控件树。
        // 不 Show 就量不到这两步，量到的是"用户永远看不到的那个版本"。
        // 实测：不 Show 时打包窗口报 0 问题，Show 之后才发现标签被裁成
        // "瓦片目""versio"、底部整排按钮被挤出窗体——检查器当时是瞎的。
        // 放到屏幕外，不打扰正在用电脑的人；量完就藏起来。
        var alreadyVisible = form.Visible;
        if (!alreadyVisible)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.ShowInTaskbar = false;
            form.Show();
            Application.DoEvents();
        }

        // 递归是必须的：只对窗体的直接子控件调 PerformLayout 的话，嵌套的
        // TableLayoutPanel 还没排过版就被量尺寸，报出来的"尺寸为 0""超出父容器"
        // 全是假的（实际踩过：第一版报了 84 处，绝大多数是这么来的）。
        form.CreateControl();
        ForceLayout(form);

        Walk(formName, form, form, problems);

        if (!alreadyVisible) form.Hide();
        return problems;
    }

    /// <summary>自顶向下强制排版。顺序不能反：父容器排完，子控件才有正确的可用空间。</summary>
    private static void ForceLayout(Control control)
    {
        // TabControl 的页签内容是**懒布局**的：没被选中过的页签根本没排过版，
        // 量出来的尺寸是默认值，会报一堆假的"超出父容器"。
        // 这里把每个页签临时选一遍，让它们都排一次。
        if (control is TabControl tabs)
        {
            // 先排 TabControl 自己：页签容器的尺寸没定，页签内容的可用空间就无从谈起。
            control.PerformLayout();
            var original = tabs.SelectedIndex;
            for (var i = 0; i < tabs.TabPages.Count; i++)
            {
                tabs.SelectedIndex = i;
                var page = tabs.TabPages[i];
                page.PerformLayout();
                foreach (Control child in page.Controls) ForceLayout(child);
                page.PerformLayout();
            }
            tabs.SelectedIndex = original;
            return;
        }

        control.PerformLayout();
        foreach (Control child in control.Controls) ForceLayout(child);
        control.PerformLayout();
    }

    private static void Walk(string formName, Control root, Control parent, List<Problem> problems)
    {
        foreach (Control child in parent.Controls)
        {
            // **不能按 Visible 过滤。** 窗体没有 Show 时，WinForms 的 Control.Visible
            // 返回的是"有效可见性"——祖先不可见就一律 false，于是所有子控件都被跳过，
            // 检查器一条都报不出来（实际踩过：对着故意做坏的窗体报 0 处）。
            // 排版在 PerformLayout 之后就已经确定了，与可不可见无关，直接查即可。
            var path = Describe(child);

            if (child.Width <= 0 || child.Height <= 0)
                problems.Add(new Problem(formName, path, $"尺寸为 {child.Width}×{child.Height}"));

            // 超出父容器：就是"内容被裁掉"。
            // Dock=Fill / Top / Bottom 的控件贴着边是正常的，所以留 1 像素余量。
            // 父容器开了 AutoScroll 时，子控件超出可视区是**设计如此**（滚动查看），
            // 不是排版错误。不排除它的话，侧栏按钮这类会一直误报。
            if (parent != root && parent is not ScrollableControl { AutoScroll: true } &&
                (child.Right > parent.ClientSize.Width + 1 ||
                 child.Bottom > parent.ClientSize.Height + 1))
            {
                problems.Add(new Problem(formName, path,
                    $"超出父容器 {Describe(parent)} " +
                    $"（{child.Right}×{child.Bottom} > {parent.ClientSize.Width}×{parent.ClientSize.Height}）"));
            }

            // 文字被截断：AutoSize 的控件（勾选框、标签、按钮）被塞进过窄的格子时，
            // WinForms **不会**把它裁到父容器外，只是把文字截掉——所以上面那条
            // "超出父容器"抓不到它。
            //
            // **用 TextRenderer 按控件自己的字体量，不要用 PreferredSize。**
            // PreferredSize 对固定宽度（AutoSize=false）的标签给不出"这段字要多少
            // 像素"，实测漏报过整屏的"瓦片目""versio"。TextRenderer 量的就是
            // 真正绘制时用的宽度，量出来的数字还能直接写进报告。
            if (TextIsClipped(child, out var needed))
            {
                problems.Add(new Problem(formName, path,
                    $"文字可能被截断（宽 {child.Width}，需要 {needed}）"));
            }

            if (child.HasChildren) Walk(formName, root, child, problems);
        }

        // 兄弟重叠：只比同一父容器下的直接子控件。
        // TabPage 之间天然"重叠"——同一时刻只显示一个，这是设计如此，不是排版错误。
        var siblings = parent.Controls.Cast<Control>()
            .Where(c => c is not TabPage)
            .ToArray();
        for (var i = 0; i < siblings.Length; i++)
        {
            for (var j = i + 1; j < siblings.Length; j++)
            {
                var a = siblings[i];
                var b = siblings[j];
                var overlap = Rectangle.Intersect(a.Bounds, b.Bounds);
                if (overlap.Width > 2 && overlap.Height > 2)
                {
                    problems.Add(new Problem(formName, Describe(a),
                        $"与 {Describe(b)} 重叠 {overlap.Width}×{overlap.Height}"));
                }
            }
        }
    }

    /// <summary>
    /// 这个控件的文字画得下吗？按控件自己的字体实测。
    ///
    /// 排除三类不该管的：
    ///   * 输入类控件（文本框、下拉、列表、进度条）——文字长了自己滚动；
    ///   * <c>Dock=Fill</c> 的控件——宽度由容器决定，本来就不按文字定宽；
    ///   * 主动折行的提示标签（AutoSize + MaximumSize，占了两行以上）。
    /// </summary>
    private static bool TextIsClipped(Control child, out int needed)
    {
        needed = 0;
        if (string.IsNullOrEmpty(child.Text)) return false;
        if (child is TextBoxBase or ComboBox or ListView or TreeView or ProgressBar) return false;
        if (child.Dock == DockStyle.Fill) return false;
        if (child is Label { MaximumSize.Width: > 0 } && child.Height > child.Font.Height * 3 / 2) return false;

        needed = TextRenderer.MeasureText(child.Text, child.Font).Width;
        return needed > child.Width + 4;
    }

    private static string Describe(Control control)
    {
        var text = control.Text?.Replace("\n", " ").Trim() ?? "";
        if (text.Length > 22) text = text[..22] + "…";
        var name = control.GetType().Name;
        return text.Length > 0 ? $"{name}(\"{text}\")" : name;
    }

    /// <summary>
    /// 把一个窗体的控件树连尺寸一起打出来。
    ///
    /// 排错用：检查器只报"哪里不对"，但要判断"为什么不对"必须看实际的宽度链路——
    /// 一个 Fixed(104) 的标签只拿到 40 像素，原因通常在它的祖先容器上。
    /// </summary>
    public static string Dump(string formName, Form form)
    {
        var sb = new StringBuilder($"=== {formName} === 窗体 {form.ClientSize.Width}x{form.ClientSize.Height}\n");
        sb.AppendLine($"    DPI={form.DeviceDpi} mode={form.AutoScaleMode} " +
                      $"design={form.AutoScaleDimensions} current={form.CurrentAutoScaleDimensions}");
        form.CreateControl();
        ForceLayout(form);
        DumpChildren(form, 0, sb);
        return sb.ToString();
    }

    private static void DumpChildren(Control parent, int depth, StringBuilder sb)
    {
        foreach (Control child in parent.Controls)
        {
            var indent = new string(' ', depth * 2);
            var text = (child.Text ?? "").Replace("\n", " ");
            if (text.Length > 18) text = text[..18] + "…";
            // 量出来的文字宽 vs 控件宽：截断的判据。AutoSize=false 的标签（FieldLabel 那种
            // 固定宽度）用 PreferredSize 量不准，必须用 TextRenderer 按实际字体量。
            var needed = text.Length > 0
                ? TextRenderer.MeasureText(text, child.Font).Width
                : 0;
            var clipped = needed > child.Width ? $" ⚠文字需要{needed}px" : "";
            sb.AppendLine($"{indent}{child.GetType().Name} [{child.Left},{child.Top} {child.Width}x{child.Height}]" +
                          $" dock={child.Dock} autosize={child.AutoSize} font={child.Font.SizeInPoints:F0}pt/{child.Font.Height}px" +
                          (text.Length > 0 ? $" \"{text}\"" : "") + clipped);
            if (depth < 6) DumpChildren(child, depth + 1, sb);
        }
    }

    public static string Report(IReadOnlyList<Problem> problems)
    {
        if (problems.Count == 0) return "布局自检：未发现硬错误。";

        var sb = new StringBuilder($"布局自检：发现 {problems.Count} 处问题\n");
        foreach (var group in problems.GroupBy(p => p.Form))
        {
            sb.AppendLine($"  [{group.Key}]");
            foreach (var problem in group.Take(20))
                sb.AppendLine($"    · {problem.Control}：{problem.Issue}");
            if (group.Count() > 20)
                sb.AppendLine($"    …另有 {group.Count() - 20} 处");
        }
        return sb.ToString();
    }
}
