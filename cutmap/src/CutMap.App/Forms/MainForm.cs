using CutMap.App.Jobs;
using CutMap.App.Ui;
using CutMap.Core.Geo;
using CutMap.Core.Terrain;
using CutMap.Core.Tools;

namespace CutMap.App.Forms;

/// <summary>
/// 主窗口。M1 阶段只有切片，但外壳按 PRD FR-11 的串行队列搭：
/// 任务是排队的、一次跑一个、可取消，日志与进度都在这里汇集。
/// 后续里程碑加的是新的任务类型与参数页，不是重做窗口。
/// </summary>
public sealed class MainForm : Form
{
    private readonly EventPump _pump = new();
    private readonly List<SliceJob> _jobs = [];
    private readonly Queue<SliceJob> _pending = new();
    private SliceJob? _current;
    private readonly CtbHome _ctbHome;

    private readonly ListView _jobList = new();
    private readonly TextBox _log = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripButton _cancelButton = new();

    public MainForm(CtbHome ctbHome)
    {
        _ctbHome = ctbHome;
        Text = "CutMap — DEM 地形切片";
        MinimumSize = new Size(820, 560);
        Size = new Size(1040, 700);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        _pump.Start();
        SetStatus("就绪");
        AppendLog($"CTB 工具：{_ctbHome.ExePath}");
    }

    private void BuildLayout()
    {
        // ---- 工具条 ----
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4) };
        var newSlice = new ToolStripButton("新建切片任务…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        newSlice.Click += (_, _) => NewSliceTask();

        // 下载是工具条上的独立入口（PRD FR-06，不在向导内），M4 实现。
        var download = new ToolStripButton("下载 DEM 数据…")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Enabled = false,
            ToolTipText = "M4 实现",
        };

        // 取消放在工具条而不是队列行上：M1 一次只跑一个任务，谁在跑是明确的。
        _cancelButton.Text = "取消当前任务";
        _cancelButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelCurrent();

        toolbar.Items.Add(newSlice);
        toolbar.Items.Add(download);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_cancelButton);

        // ---- 队列 ----
        _jobList.View = View.Details;
        _jobList.FullRowSelect = true;
        _jobList.GridLines = true;
        _jobList.HideSelection = false;
        _jobList.Columns.Add("任务", 300);
        _jobList.Columns.Add("状态", 80);
        _jobList.Columns.Add("进度", 70);
        _jobList.Columns.Add("说明", 520);

        // ---- 日志 ----
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.WordWrap = false;
        _log.BackColor = Color.FromArgb(30, 30, 30);
        _log.ForeColor = Color.Gainsboro;
        _log.Font = new Font("Consolas", 9F);
        _log.Dock = DockStyle.Fill;

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 260,
        };
        split.Panel1.Controls.Add(_jobList);
        split.Panel2.Controls.Add(_log);
        _jobList.Dock = DockStyle.Fill;

        var logHeader = new Label
        {
            Text = "日志",
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = SystemColors.ControlLight,
        };
        split.Panel2.Controls.Add(logHeader);
        logHeader.BringToFront();

        // ---- 状态栏 ----
        _progress.Visible = false;
        _progress.Width = 180;
        var statusStrip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_progress);

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
        toolbar.BringToFront();
    }

    // ---------------------------------------------------------------- 任务

    private void NewSliceTask()
    {
        using var dialog = new SliceForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var job = new SliceJob
        {
            InputPath = dialog.InputPath,
            OutputDirectory = dialog.OutputDirectory,
            MaxZoom = dialog.MaxZoom,
        };
        _jobs.Add(job);
        _pending.Enqueue(job);
        AddJobRow(job);
        AppendLog($"已入队：{job.Display}（最深 {job.MaxZoom?.ToString() ?? "自动"} 级）");
        StartNext();
    }

    private void StartNext()
    {
        if (_current is not null || _pending.Count == 0) return;

        _current = _pending.Dequeue();
        _current.Status = JobStatus.Running;
        UpdateJobRow(_current);
        _cancelButton.Enabled = true;
        SetStatus($"切片中：{Path.GetFileName(_current.InputPath)}");
        _progress.Visible = true;
        _progress.Value = 0;

        var job = _current;
        Task.Run(() => RunSlice(job));
    }

    private void RunSlice(SliceJob job)
    {
        var started = DateTime.UtcNow;
        try
        {
            var outcome = Slicer.Run(
                _ctbHome,
                new SliceRequest(job.InputPath, job.OutputDirectory, job.MaxZoom),
                onProgress: p => _pump.Post(() =>
                {
                    job.Progress = p;
                    _progress.Value = Math.Clamp((int)(p * 100), 0, 100);
                    UpdateJobRow(job);
                }),
                onLog: line => _pump.Post(() => AppendLog("  " + line)),
                cancellation: job.Cancellation.Token);

            job.Elapsed = outcome.Elapsed;
            if (outcome.Succeeded)
            {
                job.Status = JobStatus.Succeeded;
                job.StatusDetail = $"{outcome.ActualTotal:N0} 个瓦片，最深 {outcome.MaxZoom} 级";
                _pump.Post(() => AppendLog(
                    $"完成：{job.StatusDetail}（{outcome.Elapsed.TotalSeconds:F0} s）"));
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.FailureReason = outcome.FailureReason;
                job.StatusDetail = outcome.FailureReason ?? "未知原因";
                _pump.Post(() => AppendLog($"失败：{outcome.FailureReason}"));
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.StatusDetail = "已取消";
            _pump.Post(() => AppendLog("已取消"));
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.FailureReason = ex.Message;
            job.StatusDetail = ex.Message;
            _pump.Post(() => AppendLog($"失败：{ex.Message}"));
        }
        finally
        {
            job.Elapsed = job.Elapsed == TimeSpan.Zero ? DateTime.UtcNow - started : job.Elapsed;
            _pump.Post(() =>
            {
                UpdateJobRow(job);
                _current = null;
                _cancelButton.Enabled = false;
                _progress.Visible = false;
                SetStatus("就绪");
                StartNext();
            });
        }
    }

    private void CancelCurrent()
    {
        if (_current is null) return;
        AppendLog($"正在取消：{Path.GetFileName(_current.InputPath)}");
        _cancelButton.Enabled = false;
        _current.Cancellation.Cancel();
    }

    // ---------------------------------------------------------------- 视图

    private void AddJobRow(SliceJob job)
    {
        var item = new ListViewItem(job.Display) { Tag = job };
        item.SubItems.Add(JobStatusText.Of(job.Status));
        item.SubItems.Add("0%");
        item.SubItems.Add("");
        _jobList.Items.Add(item);
    }

    private void UpdateJobRow(SliceJob job)
    {
        foreach (ListViewItem item in _jobList.Items)
        {
            if (!ReferenceEquals(item.Tag, job)) continue;
            item.SubItems[1].Text = JobStatusText.Of(job.Status);
            item.SubItems[2].Text = job.Progress > 0 ? $"{job.Progress * 100:F0}%" : "";
            item.SubItems[3].Text = job.StatusDetail;
            return;
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AppendLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        _log.AppendText(stamped);
        if (_pump.Dropped > 0)
            _log.AppendText($"[警告] 有 {_pump.Dropped} 条日志因界面跟不上被丢弃{Environment.NewLine}");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _current?.Cancellation.Cancel();
        _pump.Dispose();
        base.OnFormClosed(e);
    }
}
