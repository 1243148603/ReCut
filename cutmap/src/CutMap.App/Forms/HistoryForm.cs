using System.Text.Json;
using CutMap.Core.Store;
using CutMap.App.Ui;

namespace CutMap.App.Forms;

/// <summary>
/// 任务历史（FR-09）：查看、重跑、删除单条、清空全部。
///
/// "重跑"的实际做法是**把参数回填进对应的新建对话框**，而不是照着记录直接再跑一遍。
/// PRD FR-09 说得很清楚：双击记录打开对应界面并回填，因为用户点重跑往往是想
/// 改一个参数再跑（换个输出目录、换个层级），直接跑等于逼他再填一遍。
/// </summary>
public sealed class HistoryForm : ThemedForm
{
    private readonly HistoryStore _store;
    private readonly ListView _list = new();
    private readonly Label _detail = new();
    private readonly Button _rerun = new();

    /// <summary>用户选择重跑的那条记录。对话框关闭后由调用方读取。</summary>
    public HistoryEntry? RerunRequest { get; private set; }

    public HistoryForm(HistoryStore store)
    {
        _store = store;
        Text = "任务历史";
        StartPosition = FormStartPosition.CenterParent;
        FormLayout.Resizable(this, new Size(940, 560), new Size(720, 420));
        MinimumSize = new Size(760, 420);
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
        _list.Columns.Add("时间", 150);
        _list.Columns.Add("类型", 80);
        _list.Columns.Add("状态", 70);
        _list.Columns.Add("耗时", 70);
        _list.Columns.Add("说明", 520);
        _list.SelectedIndexChanged += (_, _) => UpdateDetail();
        _list.DoubleClick += (_, _) => Rerun();

        var side = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.TopDown,
            Width = 126,
            Padding = new Padding(8),
            WrapContents = false,
        };
        _rerun.Text = "重跑…";
        _rerun.Width = 92;
        _rerun.Height = 32;
        _rerun.Click += (_, _) => Rerun();
        side.Controls.Add(_rerun);
        side.Controls.Add(MakeButton("删除", Delete));
        side.Controls.Add(MakeButton("清空全部", ClearAll));

        _detail.Dock = DockStyle.Bottom;
        _detail.Height = 76;
        _detail.Padding = new Padding(10, 6, 10, 0);
        _detail.ForeColor = SystemColors.GrayText;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 10),
        };
        var close = new Button { Text = "关闭", DialogResult = DialogResult.Cancel, Width = 84 };
        buttons.Controls.Add(close);

        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(_list);
        panel.Controls.Add(side);

        Controls.Add(panel);
        Controls.Add(_detail);
        Controls.Add(buttons);
        CancelButton = close;
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Width = 92, Height = 32 };
        button.Click += (_, _) => action();
        return button;
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var entry in _store.All)
        {
            var item = new ListViewItem(entry.StartedAt.ToLocalTime().ToString("MM-dd HH:mm:ss"))
            {
                Tag = entry,
            };
            item.SubItems.Add(entry.Type);
            item.SubItems.Add(entry.Status);
            item.SubItems.Add(entry.ElapsedSeconds >= 1
                ? TimeSpan.FromSeconds(entry.ElapsedSeconds).ToString(@"mm\:ss")
                : "<1s");
            item.SubItems.Add(entry.Summary);
            item.ForeColor = entry.Status switch
            {
                "失败" => Color.Firebrick,
                "成功" => Color.DarkGreen,
                _ => SystemColors.GrayText,
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        UpdateDetail();
    }

    private HistoryEntry? Selected =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as HistoryEntry : null;

    private void UpdateDetail()
    {
        if (Selected is not { } entry)
        {
            _detail.Text = _store.All.Count == 0
                ? "还没有任务记录。"
                : $"共 {_store.All.Count} 条记录。";
            _rerun.Enabled = false;
            return;
        }

        var lines = new List<string> { $"产物：{entry.ProductPath ?? "(无)"}" };
        if (entry.Failure is { } failure) lines.Add($"失败原因：{failure}");
        lines.Add("参数：" + Summarize(entry.ParametersJson));
        _detail.Text = string.Join(Environment.NewLine, lines);
        _rerun.Enabled = true;
    }

    private static string Summarize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var parts = document.RootElement.EnumerateObject()
                .Where(p => p.Name is "kind" or "input" or "output" or "saveDirectory"
                                    or "west" or "sourceName" or "maxZoom")
                .Select(p => $"{p.Name}={p.Value}");
            return string.Join(" ", parts);
        }
        catch (JsonException)
        {
            return "(参数无法解析)";
        }
    }

    private void Rerun()
    {
        if (Selected is not { } entry) return;
        RerunRequest = entry;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Delete()
    {
        if (Selected is not { } entry) return;
        _store.Delete(entry.Id);
        Reload();
    }

    private void ClearAll()
    {
        var count = _store.All.Count;
        if (count == 0) return;

        // FR-09 明确要求"清空需二次确认，且**先确认后清除**"。
        var answer = MessageBox.Show(this,
            $"确定要清空全部 {count} 条历史记录吗？\n\n此操作不可撤销。",
            "清空历史", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _store.Clear();
        Reload();
    }
}
