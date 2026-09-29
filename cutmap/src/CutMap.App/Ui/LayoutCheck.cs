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

    /// <summary>构造窗体并强制走一遍布局，返回发现的问题。</summary>
    public static IReadOnlyList<Problem> Inspect(string formName, Form form)
    {
        var problems = new List<Problem>();

        // 不 Show 也能算出真实布局：CreateControl 建句柄，然后**递归**走一遍排列。
        //
        // 递归是必须的：只对窗体的直接子控件调 PerformLayout 的话，嵌套的
        // TableLayoutPanel 还没排过版就被量尺寸，报出来的"尺寸为 0""超出父容器"
        // 全是假的（实际踩过：第一版报了 84 处，绝大多数是这么来的）。
        form.CreateControl();
        ForceLayout(form);

        Walk(formName, form, form, problems);
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
            // "超出父容器"抓不到它。这里比实际宽度与首选宽度。
            // Dock=Fill 的控件是故意拉伸的，不参与比较。
            if (child is not (TextBoxBase or ComboBox or ListView or TreeView or ProgressBar) &&
                child.Dock != DockStyle.Fill &&
                child.PreferredSize.Width > child.Width + 4)
            {
                problems.Add(new Problem(formName, path,
                    $"文字可能被截断（宽 {child.Width}，需要 {child.PreferredSize.Width}）"));
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

    private static string Describe(Control control)
    {
        var text = control.Text?.Replace("\n", " ").Trim() ?? "";
        if (text.Length > 22) text = text[..22] + "…";
        var name = control.GetType().Name;
        return text.Length > 0 ? $"{name}(\"{text}\")" : name;
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
