namespace CutMap.App.Ui;

/// <summary>
/// 布局辅助。
///
/// **为什么需要这一层。** 之前每个窗体手写 TableLayoutPanel，写出了一个系统性错误：
/// 把 <c>Dock = Fill</c> 的控件放进 <c>AutoSize</c> 的行里。两者互相打架——
/// Dock=Fill 会忽略控件自己的 Height，而行高又指望控件撑开，结果内容被压扁、
/// 裁掉、重叠（打包窗口那一屏就是这么来的）。79 处 Dock=Fill 里踩中这个组合的
/// 都不对。
///
/// 正确的规则只有一条：**容器设 AutoSize，让尺寸由内容决定，不要手填 Height。**
/// 这几个工厂方法把这条规则固化下来，窗体只声明"有什么"，不猜"多大"。
///
/// **类名为什么叫 FormLayout 而不是 Layout**：<c>Control.Layout</c> 是一个事件，
/// 凡是继承自 Control 的类（也就是所有窗体）都会把它继承下来，
/// 在窗体内部写 <c>Layout.Root()</c> 会被解析成那个事件，编译器报
/// "事件只能出现在 += 或 -= 的左边"。改名是最省事的绕开方式。
/// </summary>
internal static class FormLayout
{
    /// <summary>
    /// 根面板：单列、按内容撑高、四周留白。
    ///
    /// **根面板不设 AutoSize。** 试过让根和窗体都 AutoSize，结果是百分比列塌缩——
    /// AutoSize 的容器问子控件要"首选宽度"，而百分比列在没有确定宽度时只能按最小值报，
    /// 于是所有伸缩列都被挤扁、文字被截断。**窗体给定宽度、根铺满它、列再来分配**，
    /// 才是能用的组合。
    /// </summary>
    public static TableLayoutPanel Root(int padding = Theme.PagePadding)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 0,
            AutoSize = false,
            Padding = new Padding(padding),
            BackColor = Theme.Background,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return root;
    }

    /// <summary>网格：列宽由调用方给。行高一律按内容。</summary>
    public static TableLayoutPanel Grid(params ColumnStyle[] columns)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = Math.Max(columns.Length, 1),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };
        foreach (var column in columns) grid.ColumnStyles.Add(column);
        return grid;
    }

    /// <summary>固定像素宽的列。</summary>
    public static ColumnStyle Fixed(float width) => new(SizeType.Absolute, width);

    /// <summary>按比例分配、可伸缩的列。</summary>
    public static ColumnStyle Flexible(float weight = 100f) => new(SizeType.Percent, weight);

    /// <summary>分组框：按内容撑高。</summary>
    public static GroupBox Section(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, Theme.Gap),
            BackColor = Theme.Background,
            ForeColor = Theme.Muted,
            Font = Theme.BodyBold,
        };
        // **content 用 Dock=Top 而不是 Fill。**
        // GroupBox 设了 AutoSize 时，Dock=Fill 的子控件量出来的高度是 0——
        // 分组框于是塌成一条，里面所有控件都"超出父容器"。
        // Dock=Top 让它铺满宽度、高度取自己的首选值，AutoSize 的分组框再据此定高。
        content.Dock = DockStyle.Top;
        content.Margin = new Padding(0);
        box.Controls.Add(content);
        return box;
    }

    /// <summary>
    /// 加一行（网格自动增长行数）。**按顺序占列**，传 null 表示该列留空。
    ///
    /// 留空这一手是必须的：想让某个控件跨列时，得先把它后面的控件放到更右边的列去，
    /// 否则它们会挤在同一格上——实际踩过（勾选框跨两列，正好压在旁边的勾选框上，
    /// 显示成文字重叠）。
    /// </summary>
    public static void AddRow(this TableLayoutPanel grid, params Control?[] cells)
    {
        var row = grid.RowCount;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < cells.Length && i < grid.ColumnCount; i++)
        {
            if (cells[i] is { } cell) grid.Controls.Add(cell, i, row);
        }
    }

    /// <summary>加一行并让某个控件跨列。</summary>
    public static void AddRowSpanning(this TableLayoutPanel grid, Control cell, int span)
    {
        var row = grid.RowCount;
        grid.RowCount = row + 1;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(cell, 0, row);
        grid.SetColumnSpan(cell, Math.Min(span, grid.ColumnCount));
    }

    /// <summary>
    /// 西/南/东/北 四个数值输入，横排一行。
    ///
    /// **不要用"每个数一个小格子放标签+输入框"的写法**：格子按比例分配后会窄到放不下，
    /// 标签被截断、输入框溢出（实际踩过）。这里用一个 FlowLayoutPanel 横排，
    /// 每格宽度由内容决定，放不下就换行——不会挤坏。
    /// </summary>
    public static Control BoundsRow(TextBox[] boxes, string[] labels)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        for (var i = 0; i < boxes.Length; i++)
        {
            var cell = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 2, 14, 2),
                BackColor = Color.Transparent,
            };
            cell.Controls.Add(new Label
            {
                Text = labels[i],
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 4, 0),
                ForeColor = Theme.Text,
            });
            boxes[i].Width = 96;
            cell.Controls.Add(boxes[i]);
            row.Controls.Add(cell);
        }
        return row;
    }

    /// <summary>表单字段的标签。宽度固定，好让同一组里各行标签对齐。</summary>
    public static Label FieldLabel(string text, int width = 84) => new()
    {
        Text = text,
        AutoSize = false,
        Width = width,
        Height = 22,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Theme.Text,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 4, 6, 0),
    };

    /// <summary>一行按钮（右对齐）。primary 传主操作，其余为次要。</summary>
    public static FlowLayoutPanel ButtonRow(Button? primary, params Button[] secondary)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, Theme.Gap, 0, 0),
            BackColor = Color.Transparent,
        };

        if (primary is not null)
        {
            Theme.MarkPrimary(primary);
            if (primary.Width < Theme.PrimaryButtonWidth) primary.Width = Theme.PrimaryButtonWidth;
            row.Controls.Add(primary);
        }
        foreach (var button in secondary)
        {
            if (button.Width < Theme.ButtonWidth) button.Width = Theme.ButtonWidth;
            row.Controls.Add(button);
        }
        return row;
    }

    /// <summary>
    /// 参数对话框：固定尺寸。
    ///
    /// 不设 AutoSize——见 <see cref="Root"/> 里说的原因（会让百分比列塌缩）。
    /// 高度由内容决定这件事靠**行用 AutoSize** 实现：行按内容撑高，
    /// 根面板总高就是内容高，窗体高度取得比它略大即可。
    /// </summary>
    public static void FitToContent(Form form, Size size)
    {
        form.AutoSize = false;
        form.ClientSize = size;
        form.MinimumSize = size;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
    }

    /// <summary>
    /// 列表类对话框：**保持可调整大小**。
    ///
    /// 历史、预设、下载源这些窗体里装的是列表，用户会想拉大看更多行，
    /// 所以不能设 AutoSize——那会让拖动失效。只给一个合理的默认尺寸与下限。
    /// </summary>
    public static void Resizable(Form form, Size defaultSize, Size minimum)
    {
        form.AutoSize = false;
        form.MinimumSize = minimum;
        form.Size = defaultSize;
    }
}
