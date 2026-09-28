using System.Globalization;
using CutMap.Core.Geo;
using CutMap.Core.Store;

namespace CutMap.App.Forms;

/// <summary>
/// 裁剪范围预设的管理（FR-08）：内置 + 自定义，可增删改，本地 json 持久化。
///
/// 内置预设（重庆全域）**不可删除**：它是 204 教程那条命令行的口径，删了之后
/// "照着教程跑一遍"就没了参照。要改就另存为新预设。
/// </summary>
public sealed class PresetForm : Form
{
    private readonly PresetStore _store;
    private readonly ListView _list = new();

    public PresetForm(PresetStore store)
    {
        _store = store;
        Text = "裁剪范围预设";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 400);
        MinimumSize = new Size(580, 340);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        Reload();
    }

    private void BuildLayout()
    {
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.GridLines = true;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("名称", 160);
        _list.Columns.Add("范围（西 南 东 北）", 320);
        _list.Columns.Add("说明", 200);
        _list.DoubleClick += (_, _) => Edit();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 100,
            Padding = new Padding(8),
            WrapContents = false,
        };
        side.Controls.Add(MakeButton("新增…", Add));
        side.Controls.Add(MakeButton("编辑…", Edit));
        side.Controls.Add(MakeButton("删除", Delete));

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        buttons.Controls.Add(new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 84 });

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(12, 4, 12, 0),
            ForeColor = SystemColors.GrayText,
            Text = "内置预设不可删除（可编辑范围后另存为新预设）。预设存在 %APPDATA%\\CutMap\\presets.json。",
        };

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_list);
        panel.Controls.Add(side);

        Controls.Add(panel);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 84, Height = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var preset in _store.ClipPresets)
        {
            var b = preset.Box;
            var item = new ListViewItem(preset.Name + (preset.IsBuiltIn ? "（内置）" : ""))
            {
                Tag = preset,
            };
            item.SubItems.Add(string.Join(" ",
                b.West.ToString(CultureInfo.InvariantCulture),
                b.South.ToString(CultureInfo.InvariantCulture),
                b.East.ToString(CultureInfo.InvariantCulture),
                b.North.ToString(CultureInfo.InvariantCulture)));
            item.SubItems.Add(preset.Description);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private ClipPreset? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ClipPreset : null;

    private void Add()
    {
        using var dialog = new ClipPresetEditForm(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveClip(dialog.Result);
        Reload();
    }

    private void Edit()
    {
        if (Selected is not { } preset) return;
        using var dialog = new ClipPresetEditForm(preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveClip(dialog.Result);
        Reload();
    }

    private void Delete()
    {
        if (Selected is not { } preset) return;
        if (preset.IsBuiltIn)
        {
            MessageBox.Show(this, "内置预设不能删除。可以编辑它的范围后另存为新预设。",
                            "删不掉", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除预设「{preset.Name}」？", "确认",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        _store.DeleteClip(preset.Id);
        Reload();
    }
}

/// <summary>单条裁剪预设的编辑框。</summary>
public sealed class ClipPresetEditForm : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly TextBox[] _bounds = new TextBox[4];
    private readonly Label _preview = new();
    private readonly Button _ok = new();

    private readonly ClipPreset? _original;
    public ClipPreset Result { get; private set; } = PresetStore.Chongqing;

    private static readonly string[] Labels = ["西", "南", "东", "北"];

    public ClipPresetEditForm(ClipPreset? original)
    {
        _original = original;
        Text = original is null ? "新增预设" : "编辑预设";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 260);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 4,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        for (var c = 0; c < 3; c++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

        layout.Controls.Add(new Label { Text = "名称：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.Text = original?.Name ?? "";
        layout.Controls.Add(_name, 1, 0);
        layout.SetColumnSpan(_name, 3);

        layout.Controls.Add(new Label { Text = "范围：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var source = original?.Box ?? PresetStore.Chongqing.Box;
        var values = new[] { source.West, source.South, source.East, source.North };
        for (var i = 0; i < 4; i++)
        {
            var cell = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 8, 0) };
            cell.Controls.Add(new Label { Text = Labels[i], AutoSize = true, Anchor = AnchorStyles.Left });
            var box = new TextBox
            {
                Width = 86,
                Text = values[i].ToString(CultureInfo.InvariantCulture),
            };
            box.TextChanged += (_, _) => UpdatePreview();
            _bounds[i] = box;
            cell.Controls.Add(box);
            layout.Controls.Add(cell, (i % 3) + 1, 1 + i / 3);
        }

        layout.Controls.Add(new Label { Text = "说明：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _description.Dock = DockStyle.Fill;
        _description.Text = original?.Description ?? "";
        layout.Controls.Add(_description, 1, 3);
        layout.SetColumnSpan(_description, 3);

        _preview.Dock = DockStyle.Bottom;
        _preview.Height = 24;
        _preview.Padding = new Padding(14, 0, 14, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 84 };
        _ok.Text = "确定";
        _ok.Width = 84;
        _ok.Click += (_, _) => { Result = Build(); DialogResult = DialogResult.OK; Close(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);

        Controls.Add(layout);
        Controls.Add(_preview);
        Controls.Add(buttons);
        AcceptButton = _ok;
        CancelButton = cancel;
        UpdatePreview();
    }

    private ClipPreset Build() => new()
    {
        // 编辑内置项时换一个新 Id：否则下次升级换了内置定义，用户改的这份会被覆盖。
        Id = _original is { IsBuiltIn: false } ? _original.Id : Guid.NewGuid().ToString("N"),
        Name = string.IsNullOrWhiteSpace(_name.Text) ? "未命名预设" : _name.Text.Trim(),
        Description = _description.Text.Trim(),
        Bounds = [.. _bounds.Select(b => Parse(b))],
        IsBuiltIn = false,
    };

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : double.NaN;

    private void UpdatePreview()
    {
        var values = _bounds.Select(Parse).ToArray();
        if (values.Any(double.IsNaN))
        {
            _preview.Text = "四个数都要填：西 南 东 北。";
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        var box = new BBox(values[0], values[1], values[2], values[3]);
        if (!box.IsValid(out var reason))
        {
            _preview.Text = reason;
            _preview.ForeColor = Color.Firebrick;
            _ok.Enabled = false;
            return;
        }

        _preview.Text = $"范围 {box.Width:F4}° × {box.Height:F4}°";
        _preview.ForeColor = SystemColors.GrayText;
        _ok.Enabled = true;
    }
}
