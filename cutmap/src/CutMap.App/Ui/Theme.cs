namespace CutMap.App.Ui;

/// <summary>
/// 全局视觉规范。
///
/// **为什么要有这一层。** 十个窗体各自设置 BackColor / Font / Height 的结果，
/// 必然是"每页都差一点"——按钮高度有 24 也有 30，灰有好几种，间距有的 4 有的 12。
/// 那种界面不是不能用，但看起来不像一个产品。把规范收在一处，
/// 改一次全局生效，也不会再各写各的。
///
/// 用 <see cref="Apply"/> 递归套用，窗体只负责摆位置，不负责长相。
/// </summary>
internal static class Theme
{
    // ---- 配色。取自常见的企业级后台配色：浅灰底、白卡片、一个主色 ----
    public static readonly Color Background = Color.FromArgb(0xF6, 0xF7, 0xF9);
    public static readonly Color Surface = Color.White;
    public static readonly Color Border = Color.FromArgb(0xDC, 0xDF, 0xE4);
    public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x7B);
    public static readonly Color Primary = Color.FromArgb(0x1F, 0x6F, 0xEB);
    public static readonly Color PrimaryHover = Color.FromArgb(0x1A, 0x5F, 0xCC);
    public static readonly Color Danger = Color.FromArgb(0xC0, 0x39, 0x2B);
    public static readonly Color Success = Color.FromArgb(0x1A, 0x7F, 0x37);
    public static readonly Color Warning = Color.FromArgb(0xB5, 0x6A, 0x00);
    public static readonly Color Subtle = Color.FromArgb(0xEE, 0xF0, 0xF3);

    // ---- 字体 ----
    public static readonly Font Body = new("Microsoft YaHei UI", 9F);
    public static readonly Font BodyBold = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    public static readonly Font Heading = new("Microsoft YaHei UI", 12F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);

    // ---- 尺寸 ----
    public const int ButtonHeight = 30;
    public const int ButtonWidth = 92;
    public const int PrimaryButtonWidth = 104;
    public const int Gap = 8;
    public const int PagePadding = 14;

    /// <summary>把某个按钮标成主操作（实心主色）。每个界面只该有一个。</summary>
    public const string PrimaryTag = "cutmap.primary";

    public static void MarkPrimary(Button button) => button.Tag = PrimaryTag;

    /// <summary>
    /// 把某个控件排除在统一配色之外。
    ///
    /// 给少数**刻意不跟主题走**的控件用——典型是主窗口那个深色日志面板：
    /// 它要的是控制台观感，被刷成白底反而不对。没有这个开关的话，
    /// 主题会在 OnLoad 时无条件覆盖掉 BuildLayout 里设的颜色。
    /// </summary>
    private const string ExcludeTag = "cutmap.exclude";

    public static T Exclude<T>(T control) where T : Control
    {
        control.Tag = ExcludeTag;
        return control;
    }

    /// <summary>把一个窗体及其所有子控件套上规范。</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
        form.Font = Body;
        // 按字体缩放而不是按像素：不同 DPI 下布局才不会散。
        form.AutoScaleMode = AutoScaleMode.Font;
        StyleChildren(form);
    }

    private static void StyleChildren(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!Equals(child.Tag, ExcludeTag)) Style(child);
            // 子控件仍要递归：豁免的是这个控件本身的外观，不是它下面整棵子树。
            if (child.HasChildren) StyleChildren(child);
        }
    }

    private static void Style(Control control)
    {
        switch (control)
        {
            case Button button:
                StyleButton(button);
                break;

            case TextBox textBox:
                textBox.BorderStyle = BorderStyle.FixedSingle;
                textBox.BackColor = Surface;
                textBox.ForeColor = Text;
                break;

            case ComboBox combo:
                combo.FlatStyle = FlatStyle.Flat;
                combo.BackColor = Surface;
                combo.ForeColor = Text;
                break;

            case NumericUpDown numeric:
                numeric.BorderStyle = BorderStyle.FixedSingle;
                numeric.BackColor = Surface;
                break;

            case ListView list:
                list.BorderStyle = BorderStyle.FixedSingle;
                list.BackColor = Surface;
                list.ForeColor = Text;
                // 网格线是企业后台里最容易显得"上世代"的一处；用整行选中代替划分。
                list.GridLines = false;
                list.FullRowSelect = true;
                list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
                break;

            case TreeView tree:
                tree.BorderStyle = BorderStyle.FixedSingle;
                tree.BackColor = Surface;
                tree.ForeColor = Text;
                tree.HideSelection = false;
                break;

            case GroupBox group:
                group.ForeColor = Muted;
                group.Font = BodyBold;
                group.BackColor = Background;
                group.Padding = new Padding(Gap, 4, Gap, 4);
                break;

            case CheckBox check:
                check.ForeColor = Text;
                check.BackColor = Color.Transparent;
                break;

            case RadioButton radio:
                radio.ForeColor = Text;
                radio.BackColor = Color.Transparent;
                break;

            case LinkLabel link:
                link.LinkColor = Primary;
                link.ActiveLinkColor = PrimaryHover;
                break;

            case ProgressBar:
                // ProgressBar 的颜色由系统主题决定，改不了；保持默认即可。
                break;

            case Label label:
                // 已经显式设过颜色的标签（提示、错误）不覆盖。
                if (label.ForeColor == SystemColors.ControlText ||
                    label.ForeColor == SystemColors.GrayText)
                {
                    label.ForeColor = label.ForeColor == SystemColors.GrayText ? Muted : Text;
                }
                label.BackColor = Color.Transparent;
                break;

            case Panel panel:
                panel.BackColor = Background;
                break;

            case StatusStrip status:
                status.BackColor = Surface;
                status.ForeColor = Muted;
                status.SizingGrip = false;
                status.Font = Body;
                break;

            // StatusStrip 继承自 ToolStrip，**必须排在它前面**，
            // 否则会被上面那个 case 吃掉、这个分支永远不可达（编译器会报 CS8120）。
            case ToolStrip strip:
                // 工具条默认是渐变灰，放在浅色底上像另一套系统。
                strip.RenderMode = ToolStripRenderMode.System;
                strip.BackColor = Surface;
                strip.ForeColor = Text;
                strip.GripStyle = ToolStripGripStyle.Hidden;
                strip.Padding = new Padding(6, 3, 6, 3);
                strip.Font = Body;
                foreach (ToolStripItem item in strip.Items) StyleToolStripItem(item);
                break;

            case TabControl tabs:
                tabs.Font = Body;
                tabs.Padding = new Point(14, 6);
                break;
        }
    }

    private static void StyleToolStripItem(ToolStripItem item)
    {
        item.ForeColor = Text;
        item.Font = Body;
        if (item is ToolStripButton button)
        {
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
            button.Padding = new Padding(8, 2, 8, 2);
        }
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.Font = Body;

        if (Equals(button.Tag, PrimaryTag))
        {
            button.BackColor = Primary;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Primary;
            button.FlatAppearance.MouseOverBackColor = PrimaryHover;
            button.FlatAppearance.MouseDownBackColor = PrimaryHover;
        }
        else
        {
            button.BackColor = Surface;
            button.ForeColor = Text;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Subtle;
            button.FlatAppearance.MouseDownBackColor = Border;
        }

        if (button.Height < ButtonHeight) button.Height = ButtonHeight;
    }

    /// <summary>一行提示文字（次要色）。</summary>
    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        BackColor = Color.Transparent,
    };

    /// <summary>主标题。</summary>
    public static Label Heading1(string text) => new()
    {
        Text = text,
        Font = Heading,
        AutoSize = true,
        ForeColor = Text,
        BackColor = Color.Transparent,
    };

    /// <summary>状态提示。用颜色区分语义，避免到处硬编码颜色值。</summary>
    public static void SetStatus(Label label, string text, StatusKind kind)
    {
        label.Text = text;
        label.ForeColor = kind switch
        {
            StatusKind.Ok => Success,
            StatusKind.Warn => Warning,
            StatusKind.Error => Danger,
            _ => Muted,
        };
    }

    internal enum StatusKind { Normal, Ok, Warn, Error }
}
