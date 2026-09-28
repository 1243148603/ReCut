namespace CutMap.App.Forms;

public enum TaskKind
{
    Mosaic,
    Slice,
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
        ClientSize = new Size(420, 190);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var hint = new Label
        {
            Text = "本次要执行哪一步？",
            Dock = DockStyle.Fill,
            Height = 28,
        };
        layout.Controls.Add(hint, 0, 0);
        layout.SetColumnSpan(hint, 2);

        var mosaic = new Button { Text = "拼接裁剪", Dock = DockStyle.Fill, Height = 52 };
        mosaic.Click += (_, _) => Finish(TaskKind.Mosaic);
        var slice = new Button { Text = "切片", Dock = DockStyle.Fill, Height = 52 };
        slice.Click += (_, _) => Finish(TaskKind.Slice);

        layout.Controls.Add(mosaic, 0, 1);
        layout.Controls.Add(slice, 1, 1);

        var note = new Label
        {
            Text = "拼接裁剪可从多幅 DEM 产出裁剪后的 GeoTIFF（含算法元数据 json）；\n" +
                   "切片把一份 DEM 变成 Cesium 地形瓦片。",
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            Height = 44,
        };
        layout.Controls.Add(note, 0, 2);
        layout.SetColumnSpan(note, 2);

        Controls.Add(layout);
    }

    private void Finish(TaskKind kind)
    {
        Kind = kind;
        DialogResult = DialogResult.OK;
        Close();
    }
}
