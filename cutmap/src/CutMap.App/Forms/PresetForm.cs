using System.Globalization;
using CutMap.Core.Geo;
using CutMap.Core.Store;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 预设管理（FR-08）。两页签：
///
///   **裁剪范围**——只管一个矩形，给"我就想换个范围"的场景
///   **任务预设**——整套参数（范围/层级/layer.json/导出选项），新建任务时一键带出
///
/// 两者并存是因为用途不同：前者是"这个范围我常用"，后者是"这整套配置我常用"。
/// 内置项都不可删除（可编辑后另存为新预设）——删了之后"照着教程跑一遍"就没了参照。
/// </summary>
public sealed class PresetForm : ThemedForm
{
    private readonly PresetStore _store;
    private readonly ListView _clipList = new();
    private readonly ListView _taskList = new();

    public PresetForm(PresetStore store)
    {
        _store = store;
        Text = "预设管理";
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.Resizable(this, new Size(800, 520), new Size(640, 400));
        MinimumSize = new Size(640, 400);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        Reload();
    }

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        tabs.TabPages.Add(BuildClipPage());
        tabs.TabPages.Add(BuildTaskPage());

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(14, 4, 14, 0),
            ForeColor = SystemColors.GrayText,
            Text = "内置预设不可删除（可编辑后另存为新预设）。预设存在 %APPDATA%\\CutMap\\presets.json。",
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        buttons.Controls.Add(new Button { Text = "关闭", DialogResult = DialogResult.OK, Width = 84 });

        Controls.Add(tabs);
        Controls.Add(hint);
        Controls.Add(buttons);
    }

    private TabPage BuildClipPage()
    {
        _clipList.View = View.Details;
        _clipList.FullRowSelect = true;
        _clipList.GridLines = true;
        _clipList.Dock = DockStyle.Fill;
        _clipList.Columns.Add("名称", 160);
        _clipList.Columns.Add("范围（西 南 东 北）", 300);
        _clipList.Columns.Add("说明", 220);
        _clipList.DoubleClick += (_, _) => EditClip();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 122,
            Padding = new Padding(8),
            WrapContents = false,
            // 容器矮于内容时滚动，而不是把按钮挤出去。
            // 侧栏的实际高度取决于页签容器，用滚动兜底比继续猜尺寸可靠。
            AutoScroll = true,
        };
        side.Controls.Add(MakeButton("新增…", AddClip));
        side.Controls.Add(MakeButton("编辑…", EditClip));
        side.Controls.Add(MakeButton("删除", DeleteClip));

        var page = new TabPage("裁剪范围");
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_clipList);
        panel.Controls.Add(side);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage BuildTaskPage()
    {
        _taskList.View = View.Details;
        _taskList.FullRowSelect = true;
        _taskList.GridLines = true;
        _taskList.Dock = DockStyle.Fill;
        _taskList.Columns.Add("名称", 180);
        _taskList.Columns.Add("范围", 150);
        _taskList.Columns.Add("切片", 90);
        _taskList.Columns.Add("layer.json", 220);
        _taskList.DoubleClick += (_, _) => EditTask();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 122,
            Padding = new Padding(8),
            WrapContents = false,
            // 容器矮于内容时滚动，而不是把按钮挤出去。
            // 侧栏的实际高度取决于页签容器，用滚动兜底比继续猜尺寸可靠。
            AutoScroll = true,
        };
        side.Controls.Add(MakeButton("新增…", AddTask));
        side.Controls.Add(MakeButton("编辑…", EditTask));
        side.Controls.Add(MakeButton("删除", DeleteTask));

        var page = new TabPage("任务预设");
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_taskList);
        panel.Controls.Add(side);
        page.Controls.Add(panel);
        return page;
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 84, Height = 30 };
        button.Click += (_, _) => action();
        return button;
    }

    private void Reload()
    {
        _clipList.BeginUpdate();
        _clipList.Items.Clear();
        foreach (var preset in _store.ClipPresets)
        {
            var box = preset.Box;
            var item = new ListViewItem(preset.Name + (preset.IsBuiltIn ? "（内置）" : "")) { Tag = preset };
            item.SubItems.Add(Format(box));
            item.SubItems.Add(preset.Description);
            _clipList.Items.Add(item);
        }
        _clipList.EndUpdate();

        _taskList.BeginUpdate();
        _taskList.Items.Clear();
        foreach (var preset in _store.TaskPresets)
        {
            var item = new ListViewItem(preset.Name + (preset.IsBuiltIn ? "（内置）" : "")) { Tag = preset };
            item.SubItems.Add(preset.ClipBox is { } box2 ? Format(box2)
                            : preset.BoundaryCodes is { Length: > 0 } ? $"行政区划 {preset.BoundaryCodes.Length} 个"
                            : "(未设范围)");
            item.SubItems.Add(preset.MaxZoom is { } z ? $"{z} 级" : "自动");
            item.SubItems.Add($"{preset.LayerName} v{preset.LayerVersion}");
            _taskList.Items.Add(item);
        }
        _taskList.EndUpdate();
    }

    private static string Format(BBox box) => string.Join(" ",
        box.West.ToString(CultureInfo.InvariantCulture),
        box.South.ToString(CultureInfo.InvariantCulture),
        box.East.ToString(CultureInfo.InvariantCulture),
        box.North.ToString(CultureInfo.InvariantCulture));

    // ---------------------------------------------------------------- 裁剪范围

    private ClipPreset? SelectedClip =>
        _clipList.SelectedItems.Count > 0 ? _clipList.SelectedItems[0].Tag as ClipPreset : null;

    private void AddClip()
    {
        using var dialog = new ClipPresetEditForm(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveClip(dialog.Result);
        Reload();
    }

    private void EditClip()
    {
        if (SelectedClip is not { } preset) return;
        using var dialog = new ClipPresetEditForm(preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveClip(dialog.Result);
        Reload();
    }

    private void DeleteClip()
    {
        if (SelectedClip is not { } preset) return;
        if (!ConfirmDelete(preset.IsBuiltIn, preset.Name)) return;
        _store.DeleteClip(preset.Id);
        Reload();
    }

    // ---------------------------------------------------------------- 任务预设

    private TaskPreset? SelectedTask =>
        _taskList.SelectedItems.Count > 0 ? _taskList.SelectedItems[0].Tag as TaskPreset : null;

    private void AddTask()
    {
        using var dialog = new TaskPresetEditForm(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveTask(dialog.Result);
        Reload();
    }

    private void EditTask()
    {
        if (SelectedTask is not { } preset) return;
        using var dialog = new TaskPresetEditForm(preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _store.SaveTask(dialog.Result);
        Reload();
    }

    private void DeleteTask()
    {
        if (SelectedTask is not { } preset) return;
        if (!ConfirmDelete(preset.IsBuiltIn, preset.Name)) return;
        _store.DeleteTask(preset.Id);
        Reload();
    }

    private bool ConfirmDelete(bool isBuiltIn, string name)
    {
        if (isBuiltIn)
        {
            MessageBox.Show(this, "内置预设不能删除。可以编辑它后另存为新预设。",
                            "删不掉", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        return MessageBox.Show(this, $"删除预设「{name}」？", "确认",
                              MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
    }
}

/// <summary>单条裁剪范围预设的编辑框。</summary>
public sealed class ClipPresetEditForm : ThemedForm
{
    private readonly TextBox _name = new();
    private readonly TextBox _description = new();
    private readonly TextBox[] _bounds = new TextBox[4];
    private readonly Label _preview = new();
    private readonly Button _ok = new();

    private readonly ClipPreset? _original;
    // 不再拿"重庆全域"当初始值：新增预设时是空白的，值由 Build() 在按下确定时产出。
    public ClipPreset Result { get; private set; } = new() { Id = "", Name = "", Bounds = [0, 0, 0, 0] };

    private static readonly string[] Labels = ["西", "南", "东", "北"];

    public ClipPresetEditForm(ClipPreset? original)
    {
        _original = original;
        Text = original is null ? "新增裁剪范围预设" : "编辑裁剪范围预设";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.FitToContent(this, new Size(580, 280));
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 4,
            RowCount = 3,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        for (var c = 0; c < 3; c++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

        layout.Controls.Add(new Label { Text = "名称：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _name.Dock = DockStyle.Fill;
        _name.Text = original?.Name ?? "";
        layout.Controls.Add(_name, 1, 0);
        layout.SetColumnSpan(_name, 3);

        layout.Controls.Add(new Label { Text = "范围：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        // 新增时四个框留空（不再预填"重庆全域"）；编辑时把原有范围填进去
        // ——原来 values 算出来却没写进 TextBox，编辑已有预设时框里是空的。
        var values = original is null
            ? ["", "", "", ""]
            : new[] { original.Box.West, original.Box.South, original.Box.East, original.Box.North }
                .Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray();
        // 四个数值输入用共享的行构造器：每格宽度按内容定，不会挤到截断。
        for (var i = 0; i < _bounds.Length; i++)
        {
            _bounds[i] = new TextBox { Text = values[i] };
            _bounds[i].TextChanged += (_, _) => UpdatePreview();
        }
        layout.Controls.Add(FormLayout.BoundsRow(_bounds, Labels), 1, 1);

        layout.Controls.Add(new Label { Text = "说明：", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _description.Dock = DockStyle.Fill;
        _description.Text = original?.Description ?? "";
        layout.Controls.Add(_description, 1, 2);
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
        Bounds = [.. _bounds.Select(Parse)],
        IsBuiltIn = false,
    };

    private static double Parse(TextBox box) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : double.NaN;

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
