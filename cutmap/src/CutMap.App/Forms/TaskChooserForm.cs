using CutMap.Core.Store;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

public enum TaskKind
{
    Mosaic,
    Slice,
    Package,
}

/// <summary>
/// 选要跑哪一步，以及用哪套预设（FR-08）。
///
/// 这是 PRD FR-01「本次要执行哪些步骤」勾选的临时形态——M6 之后仍是这个形式，
/// 用三个按钮而不是"勾选后向导跳过页"。差别在于：勾选式适合"一次跑多步"，
/// 而现在每步都是独立任务，选一个、跑一个更直白。
/// </summary>
public sealed class TaskChooserForm : ThemedForm
{
    private readonly PresetStore _presets = new();
    private readonly ComboBox _preset = new();
    private readonly Label _presetNote = new();

    public TaskKind Kind { get; private set; } = TaskKind.Mosaic;

    /// <summary>用户选的预设；"不使用预设"时为 null。</summary>
    public TaskPreset? ChosenPreset { get; private set; }

    public TaskChooserForm()
    {
        Text = "新建任务";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(600, 280);
        Font = new Font("Microsoft YaHei UI", 9F);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 4,
        };
        for (var c = 0; c < 3; c++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));

        // ---- 预设 ----
        var presetRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        presetRow.Controls.Add(new Label
        {
            Text = "预设：",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 6, 0, 0),
        });
        _preset.DropDownStyle = ComboBoxStyle.DropDownList;
        _preset.Width = 300;
        _preset.Items.Add("（不使用预设，全部手填）");
        foreach (var p in _presets.TaskPresets) _preset.Items.Add(p);
        _preset.SelectedIndex = 0;
        _preset.SelectedIndexChanged += (_, _) => UpdatePresetNote();
        presetRow.Controls.Add(_preset);
        var manage = new Button { Text = "管理…", Width = 72 };
        manage.Click += (_, _) => ManagePresets();
        presetRow.Controls.Add(manage);
        layout.Controls.Add(presetRow, 0, 0);
        layout.SetColumnSpan(presetRow, 3);

        _presetNote.Dock = DockStyle.Fill;
        _presetNote.Height = 42;
        _presetNote.AutoEllipsis = true;
        _presetNote.ForeColor = SystemColors.GrayText;
        layout.Controls.Add(_presetNote, 0, 1);
        layout.SetColumnSpan(_presetNote, 3);

        // ---- 步骤 ----
        var hint = new Label
        {
            Text = "本次要执行哪一步？",
            Dock = DockStyle.Fill,
            Height = 24,
        };
        layout.Controls.Add(hint, 0, 2);
        layout.SetColumnSpan(hint, 3);

        layout.Controls.Add(MakeButton("拼接裁剪", TaskKind.Mosaic), 0, 3);
        layout.Controls.Add(MakeButton("切片", TaskKind.Slice), 1, 3);
        layout.Controls.Add(MakeButton("打包", TaskKind.Package), 2, 3);

        Controls.Add(layout);
        UpdatePresetNote();
    }

    private void ManagePresets()
    {
        using var dialog = new PresetForm(_presets);
        dialog.ShowDialog(this);

        var previous = ChosenPreset?.Id;
        _preset.Items.Clear();
        _preset.Items.Add("（不使用预设，全部手填）");
        foreach (var p in _presets.TaskPresets) _preset.Items.Add(p);

        var restored = previous is null ? 0 : Math.Max(0, _preset.Items.IndexOf(
            _presets.TaskPresets.FirstOrDefault(p => p.Id == previous)!));
        _preset.SelectedIndex = restored;
        UpdatePresetNote();
    }

    private void UpdatePresetNote()
    {
        ChosenPreset = _preset.SelectedItem as TaskPreset;
        if (ChosenPreset is null)
        {
            _presetNote.Text = "选一个预设可以把范围、层级、layer.json 字段、导出选项一次带出。";
            return;
        }

        var parts = new List<string>();
        if (ChosenPreset.ClipBox is { } box) parts.Add($"裁剪 {box}");
        if (ChosenPreset.BoundaryCodes is { Length: > 0 }) parts.Add($"行政区划 {ChosenPreset.BoundaryCodes.Length} 个");
        parts.Add(ChosenPreset.MaxZoom is { } z ? $"最深 {z} 级" : "层级自动");
        parts.Add(ChosenPreset.Resampling);
        parts.Add($"layer：{ChosenPreset.LayerName} v{ChosenPreset.LayerVersion}");
        if (ChosenPreset.ZipTiles) parts.Add("瓦片 zip");
        if (ChosenPreset.ZipAlgo) parts.Add("算法 zip");

        _presetNote.Text = string.Join("，", parts);
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
