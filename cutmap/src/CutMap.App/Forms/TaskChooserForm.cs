namespace CutMap.App.Forms;

public enum TaskKind
{
    Mosaic,
    Slice,
    Package,
}

/// <summary>
/// 选要跑哪一步。这是 PRD FR-01「本次要执行哪些步骤」勾选的临时形态——
/// M6 会做成向导里的一组复选框（拼接裁剪 / 切片 / 打包），此处先用最小可用版本。
/// </summary>
public sealed class TaskChooserForm : Form
{
    public TaskKind Kind { get; private set; } = TaskKind.Mosaic;

    public TaskChooserForm()
    {
        Text = "新建任务";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 210);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 3,
        };
        for (var c = 0; c < 3; c++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));

        var hint = new Label
        {
            Text = "本次要执行哪一步？（三步可各自独立跑）",
            Dock = DockStyle.Fill,
            Height = 28,
        };
        layout.Controls.Add(hint, 0, 0);
        layout.SetColumnSpan(hint, 3);

        layout.Controls.Add(MakeButton("拼接裁剪", TaskKind.Mosaic), 0, 1);
        layout.Controls.Add(MakeButton("切片", TaskKind.Slice), 1, 1);
        layout.Controls.Add(MakeButton("打包", TaskKind.Package), 2, 1);

        var note = new Label
        {
            Text = "拼接裁剪：多幅 DEM → 裁剪后的 GeoTIFF（可带算法元数据 json）\n" +
                   "切片：一份 DEM → Cesium 地形瓦片\n" +
                   "打包：给已有的瓦片目录写 layer.json、或把产物打成 zip（不必重跑切片）",
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Height = 58,
        };
        layout.Controls.Add(note, 0, 2);
        layout.SetColumnSpan(note, 3);

        Controls.Add(layout);
    }

    private Button MakeButton(string text, TaskKind kind)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, Height = 56 };
        button.Click += (_, _) => Finish(kind);
        return button;
    }

    private void Finish(TaskKind kind)
    {
        Kind = kind;
        DialogResult = DialogResult.OK;
        Close();
    }
}
