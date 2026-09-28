using CutMap.App.Jobs;
using CutMap.App.Ui;
using CutMap.Core.Tools;

namespace CutMap.App.Forms;

/// <summary>
/// 主窗口。按 PRD FR-11 的串行队列搭：任务排队、一次跑一个、可取消，
/// 日志与进度都汇集在这里。加任务类型时只加一个 <see cref="Job"/> 子类和一个
/// 参数对话框，不动这个窗口。
/// </summary>
public sealed class MainForm : Form
{
    private readonly EventPump _pump = new();
    private readonly List<Job> _jobs = [];
    private readonly Queue<Job> _pending = new();
    private Job? _current;
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
        MinimumSize = new Size(860, 580);
        Size = new Size(1080, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        _pump.Start();
        SetStatus("就绪");
        AppendLog($"CTB 工具：{_ctbHome.ExePath}");
    }

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4) };

        var newMosaic = new ToolStripButton("新建任务…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        newMosaic.Click += (_, _) => NewTask();

        // 下载是工具条上的独立入口（PRD FR-06，不在向导内），M4 实现。
        var download = new ToolStripButton("下载 DEM 数据…")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Enabled = false,
            ToolTipText = "M4 实现",
        };

        _cancelButton.Text = "取消当前任务";
        _cancelButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelCurrent();

        toolbar.Items.Add(newMosaic);
        toolbar.Items.Add(download);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_cancelButton);

        _jobList.View = View.Details;
        _jobList.FullRowSelect = true;
        _jobList.GridLines = true;
        _jobList.HideSelection = false;
        _jobList.Columns.Add("类型", 80);
        _jobList.Columns.Add("任务", 320);
        _jobList.Columns.Add("状态", 70);
        _jobList.Columns.Add("进度", 60);
        _jobList.Columns.Add("说明", 520);

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

    /// <summary>
    /// 新建任务向导的临时形态：M2 阶段只有"拼接裁剪"和"切片"两种，
    /// 用两个对话框顶替；M6 会做成 PRD FR-01 描述的带步骤勾选的向导。
    /// </summary>
    private void NewTask()
    {
        using var chooser = new TaskChooserForm();
        if (chooser.ShowDialog(this) != DialogResult.OK) return;

        Job? job = chooser.Kind switch
        {
            TaskKind.Mosaic => BuildMosaicJob(),
            TaskKind.Slice => BuildSliceJob(),
            TaskKind.Package => BuildPackageJob(),
            _ => null,
        };

        if (job is null) return;
        _jobs.Add(job);
        _pending.Enqueue(job);
        AddJobRow(job);
        AppendLog($"已入队 [{job.TypeName}] {job.Display}");
        StartNext();
    }

    private MosaicJob? BuildMosaicJob()
    {
        using var dialog = new MosaicForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new MosaicJob
        {
            InputPaths = dialog.InputPaths,
            OutputPath = dialog.OutputPath,
            Target = dialog.Target,
            Resampling = dialog.Resampling,
            WriteMetadata = dialog.WriteMetadata,
        };
    }

    private SliceJob? BuildSliceJob()
    {
        using var dialog = new SliceForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new SliceJob
        {
            InputPath = dialog.InputPath,
            OutputDirectory = dialog.OutputDirectory,
            MaxZoom = dialog.MaxZoom,
        };
    }

    private PackageJob? BuildPackageJob()
    {
        using var dialog = new PackageForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new PackageJob
        {
            OutputDirectory = dialog.OutputDirectory,
            TilesDirectory = dialog.TilesDirectory,
            AlgoTifPath = dialog.AlgoTifPath,
            ZipTiles = dialog.ZipTiles,
            ZipAlgo = dialog.ZipAlgo,
            LayerName = dialog.LayerName,
            LayerDescription = dialog.LayerDescription,
            LayerVersion = dialog.LayerVersion,
        };
    }

    private void StartNext()
    {
        if (_current is not null || _pending.Count == 0) return;

        var job = _pending.Dequeue();
        _current = job;
        job.Status = JobStatus.Running;
        UpdateJobRow(job);
        _cancelButton.Enabled = true;
        SetStatus($"{job.TypeName}：{job.Display}");
        _progress.Visible = true;
        _progress.Value = 0;

        Task.Run(() => RunJob(job));
    }

    private void RunJob(Job job)
    {
        try
        {
            var result = job.Execute(
                _ctbHome,
                onProgress: p => _pump.Post(() =>
                {
                    job.Progress = p;
                    _progress.Value = Math.Clamp((int)(p * 100), 0, 100);
                    UpdateJobRow(job);
                }),
                onLog: line => _pump.Post(() => AppendLog("  " + line)),
                cancellation: job.Cancellation.Token);

            if (result.Succeeded)
            {
                job.Status = JobStatus.Succeeded;
                job.StatusDetail = result.Summary ?? "完成";
                job.ProductPath = result.ProductPath;
                _pump.Post(() => AppendLog($"完成：{job.StatusDetail}"));
            }
            else
            {
                job.Status = JobStatus.Failed;
                job.FailureReason = result.FailureReason;
                job.StatusDetail = result.FailureReason ?? "未知原因";
                _pump.Post(() => AppendLog($"失败：{job.FailureReason}"));
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
        AppendLog($"正在取消：{_current.Display}");
        _cancelButton.Enabled = false;
        _current.Cancellation.Cancel();
    }

    // ---------------------------------------------------------------- 视图

    private void AddJobRow(Job job)
    {
        var item = new ListViewItem(job.TypeName) { Tag = job };
        item.SubItems.Add(job.Display);
        item.SubItems.Add(JobStatusText.Of(job.Status));
        item.SubItems.Add("0%");
        item.SubItems.Add("");
        _jobList.Items.Add(item);
    }

    private void UpdateJobRow(Job job)
    {
        foreach (ListViewItem item in _jobList.Items)
        {
            if (!ReferenceEquals(item.Tag, job)) continue;
            item.SubItems[2].Text = JobStatusText.Of(job.Status);
            item.SubItems[3].Text = job.Progress > 0 ? $"{job.Progress * 100:F0}%" : "";
            item.SubItems[4].Text = job.StatusDetail;
            return;
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AppendLog(string line)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
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
