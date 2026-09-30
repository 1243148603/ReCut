using CutMap.App.Jobs;
using CutMap.App.Ui;
using System.Text.Json;
using CutMap.Core.Store;
using CutMap.Core.Tools;

namespace CutMap.App.Forms;

/// <summary>
/// 主窗口。按 PRD FR-11 的串行队列搭：任务排队、一次跑一个、可挂起、可移除、可取消，
/// 日志与进度都汇集在这里。加任务类型时只加一个 <see cref="Job"/> 子类和一个
/// 参数对话框，不动这个窗口。
/// </summary>
public sealed class MainForm : ThemedForm
{
    private readonly EventPump _pump = new();
    private readonly List<Job> _jobs = [];
    private readonly List<Job> _pending = [];
    private readonly HistoryStore _history = new();
    private Job? _current;
    private readonly CtbHome _ctbHome;

    private readonly ListView _jobList = new();
    private readonly TextBox _log = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripButton _cancelButton = new();
    private readonly ToolStripButton _pauseButton = new();
    private readonly ToolStripButton _queueButton = new();
    private bool _queuePaused;
    private readonly ToolStripMenuItem _suspendItem = new("挂起");
    private readonly ToolStripMenuItem _removeItem = new("移除");

    private readonly string _logFilePath;

    public MainForm(CtbHome ctbHome)
    {
        _ctbHome = ctbHome;
        _logFilePath = CreateLogFile();

        Text = "CutMap — DEM 地形切片";
        MinimumSize = new Size(900, 600);
        Size = new Size(1120, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildLayout();
        _pump.Start();
        SetStatus("就绪");
        AppendLog($"CTB 工具：{_ctbHome.ExePath}");
        AppendLog($"日志文件：{_logFilePath}");
        AppendLog($"配置目录：{AppPaths.DataDirectory}");
    }

    // ---------------------------------------------------------------- 布局

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4) };

        var newMosaic = new ToolStripButton("新建任务…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        newMosaic.Click += (_, _) => NewTask();

        // 一键生产：拼接裁剪 → 切片 → 打包 一次收参数、串行跑完（复用队列，不另起调度）。
        var pipeline = new ToolStripButton("一键生产…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        pipeline.Click += (_, _) => NewPipeline();

        // 下载是工具条上的独立入口（PRD FR-06，不在新建任务向导内）。
        var download = new ToolStripButton("下载 DEM 数据…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        download.Click += (_, _) => NewDownloadTask();

        var history = new ToolStripButton("历史…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        history.Click += (_, _) => ShowHistory();

        var exportLog = new ToolStripButton("导出日志…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        exportLog.Click += (_, _) => ExportLog();

        _pauseButton.Text = "暂停";
        _pauseButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _pauseButton.Enabled = false;
        _pauseButton.Click += (_, _) => TogglePause();

        _cancelButton.Text = "取消当前任务";
        _cancelButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelCurrent();

        // 队列级暂停（FR-11）与"运行中暂停"是两回事，所以是两个按钮：
        //   运行中暂停：只对下载有效，停的是**当前这个任务**
        //   队列暂停：停的是**调度**，当前任务自然跑完，之后不再开新的
        _queueButton.Text = "暂停队列";
        _queueButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        _queueButton.Click += (_, _) => ToggleQueue();

        toolbar.Items.Add(newMosaic);
        toolbar.Items.Add(pipeline);
        toolbar.Items.Add(download);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(_pauseButton);
        toolbar.Items.Add(_queueButton);
        toolbar.Items.Add(_cancelButton);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(history);
        toolbar.Items.Add(exportLog);

        _jobList.View = View.Details;
        _jobList.FullRowSelect = true;
        _jobList.GridLines = true;
        _jobList.HideSelection = false;
        _jobList.Columns.Add("类型", 80);
        _jobList.Columns.Add("任务", 320);
        _jobList.Columns.Add("状态", 80);
        _jobList.Columns.Add("进度", 60);
        _jobList.Columns.Add("说明", 540);

        // 挂起/移除走右键菜单：这两个动作都是对"某一行"的，放在工具条上会让人
        // 分不清操作的是哪一条。（PRD FR-11：已开跑的任务不可移除，先取消。）
        var menu = new ContextMenuStrip();
        _suspendItem.Click += (_, _) => ToggleSuspend();
        _removeItem.Click += (_, _) => RemoveSelected();
        menu.Items.Add(_suspendItem);
        menu.Items.Add(_removeItem);
        menu.Opening += (_, _) => UpdateContextMenu();
        _jobList.ContextMenuStrip = menu;

        // 日志面板刻意用深色控制台外观，不跟主题走。见 Theme.Exclude。
        Theme.Exclude(_log);
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
            SplitterDistance = 280,
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
        // 同样不 BringToFront：填充分率先加、边条后加，停靠顺序本来就是对的；
        // 一提 z 序，填充控件就把整块抢回去，标题压在日志正文上（自检实测重叠 1104×22）。
        split.Panel2.Controls.Add(logHeader);

        _progress.Visible = false;
        _progress.Width = 180;
        var statusStrip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_progress);

        // 顺序不能动：填充的先加、边条的后加（WinForms 从后往前停靠）。
        // **不要在这里 BringToFront()**：它把工具条挪到 z 序最前，停靠顺序就反了——
        // 分割容器从 y=0 开始铺，工具条再压在上面 43px，任务列表的表头被盖住
        // （自检实测：分割容器 [0,0 1647x995]，正确应为 [0,43 …]）。
        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
    }

    // ---------------------------------------------------------------- 新建

    private void NewTask()
    {
        using var chooser = new TaskChooserForm();
        if (chooser.ShowDialog(this) != DialogResult.OK) return;

        var preset = chooser.ChosenPreset;
        if (preset is not null) AppendLog($"使用预设：{preset.Name}");

        Job? job = chooser.Kind switch
        {
            TaskKind.Mosaic => BuildMosaicJob(preset),
            TaskKind.Slice => BuildSliceJob(preset),
            TaskKind.Package => BuildPackageJob(preset),
            _ => null,
        };
        if (job is not null) Enqueue(job);
    }

    private void NewDownloadTask()
    {
        using var dialog = new DownloadForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        AppendLog($"下载源：{dialog.SelectedSource.Name}");
        AppendLog($"  展开后的首个 URL：{dialog.SelectedSource.Expand(dialog.Tiles[0])}");

        Enqueue(new DownloadJob
        {
            Source = dialog.SelectedSource,
            Tiles = dialog.Tiles,
            SaveDirectory = dialog.SaveDirectory,
            Area = dialog.Area,
        });
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
            Boundaries = dialog.Boundaries,
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

    private void Enqueue(Job job)
    {
        _jobs.Add(job);
        _pending.Add(job);
        AddJobRow(job);
        AppendLog($"已入队 [{job.TypeName}] {job.Display}");
        StartNext();
    }

    // ---------------------------------------------------------------- 队列

    /// <summary>FR-11 的队列级暂停：不再开始新任务，正在运行的自然跑完。</summary>
    private void ToggleQueue()
    {
        _queuePaused = !_queuePaused;
        _queueButton.Text = _queuePaused ? "恢复队列" : "暂停队列";

        if (_queuePaused)
        {
            AppendLog("队列已暂停：当前任务跑完后不再开始新任务。");
            SetStatus(_current is null ? "队列已暂停" : $"{_current.TypeName}：{_current.Display}（队列已暂停）");
            return;
        }

        AppendLog("队列已恢复。");
        SetStatus("就绪");
        StartNext();
    }

    private void StartNext()
    {
        if (_current is not null) return;
        if (_queuePaused) return;

        // 挂起的任务留在队列里但不被调度（FR-11），所以这里挑的是
        // "第一个没被挂起的"，而不是简单的队首。
        var job = _pending.FirstOrDefault(j => !j.IsSuspended && j.Status == JobStatus.Pending);
        if (job is null) return;

        _current = job;
        job.Status = JobStatus.Running;
        UpdateJobRow(job);
        _cancelButton.Enabled = true;
        _pauseButton.Enabled = true;
        _pauseButton.Text = "暂停";
        SetStatus($"{job.TypeName}：{job.Display}");
        _progress.Visible = true;
        _progress.Value = 0;

        Task.Run(() => RunJob(job));
    }

    private void RunJob(Job job)
    {
        var startedAt = DateTimeOffset.Now;
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

                // FR-06：下载成功后给一个「继续处理这批 DEM」的入口。
                // 下载被摘成独立入口之后，链路不再一次跑完，这一步是补偿。
                if (job is DownloadJob download)
                    _pump.Post(() => OfferContinue(download));
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
            RecordHistory(job, startedAt);
            _pump.Post(() =>
            {
                UpdateJobRow(job);
                // 一键生产的链路：这一环没成功，后面的不用再试了。
                if (job.ChainId is { } chain && job.Status is JobStatus.Failed or JobStatus.Cancelled)
                    SkipChainRemainder(chain);
                _current = null;
                _cancelButton.Enabled = false;
                _pauseButton.Enabled = false;
                _pauseButton.Text = "暂停";
                _progress.Visible = false;
                var waiting = _pending.Count(j => !j.IsSuspended && j.Status == JobStatus.Pending);
                SetStatus(_queuePaused
                    ? $"队列已暂停（{waiting} 个任务等待中）"
                    : "就绪");
                StartNext();
            });
        }
    }

    /// <summary>任务进终态时落一条历史（FR-09）。取消与移除的区别就在这里：取消写，移除不写。</summary>
    /// <summary>
    /// 一键生产：一个对话框收齐三段参数，然后按序入队
    /// 拼接裁剪 → 切片 → 打包。三个任务共享一个 ChainId；任一环失败/取消，
    /// 剩下的一律跳过（见 <see cref="SkipChainRemainder"/>）。
    /// </summary>
    private void NewPipeline()
    {
        using var dialog = new PipelineForm();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var chain = Guid.NewGuid().ToString("N")[..8];
        var tif = dialog.AlgoTifPath;
        var tiles = dialog.TilesDirectory;
        AppendLog($"一键生产：输出到 {dialog.RootDirectory}（中间产物路径自动推导）");

        Enqueue(new MosaicJob
        {
            InputPaths = dialog.InputPaths,
            OutputPath = tif,
            Target = dialog.Target,
            Boundaries = dialog.Boundaries,
            ChainId = chain,
        });
        Enqueue(new SliceJob
        {
            InputPath = tif,
            OutputDirectory = tiles,
            MaxZoom = null,
            ChainId = chain,
        });
        Enqueue(new PackageJob
        {
            OutputDirectory = dialog.RootDirectory,
            TilesDirectory = tiles,
            AlgoTifPath = tif,
            ZipTiles = dialog.ZipTiles,
            ZipAlgo = dialog.ZipAlgo,
            LayerName = dialog.LayerName,
            LayerDescription = dialog.LayerDescription,
            LayerVersion = dialog.LayerVersion,
            ChainId = chain,
        });
    }

    /// <summary>一键生产的上游没成功时，把同链还在排队的任务标成"已跳过"。</summary>
    private void SkipChainRemainder(string chainId)
    {
        foreach (var pending in _pending
                     .Where(j => j.ChainId == chainId && j.Status == JobStatus.Pending)
                     .ToArray())
        {
            pending.Status = JobStatus.Cancelled;
            pending.StatusDetail = "上游失败，已跳过";
            UpdateJobRow(pending);
            AppendLog($"已跳过 [{pending.TypeName}] {pending.Display}（一键生产的上游环节没成功）");
        }
    }

    private void RecordHistory(Job job, DateTimeOffset startedAt)
    {
        if (job.Status == JobStatus.Pending) return;
        try
        {
            _history.Add(new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = job.TypeName,
                StartedAt = startedAt,
                ElapsedSeconds = (DateTimeOffset.Now - startedAt).TotalSeconds,
                Status = JobStatusText.Of(job.Status),
                Summary = job.StatusDetail,
                ProductPath = job.ProductPath,
                Failure = job.FailureReason,
                ParametersJson = job.ParametersJson,
            });
        }
        catch (Exception ex)
        {
            _pump.Post(() => AppendLog($"（历史写入失败：{ex.Message}）"));
        }
    }

    private void ToggleSuspend()
    {
        if (SelectedJob() is not { } job) return;
        if (job.IsTerminal)
        {
            AppendLog("已结束的任务不能挂起。");
            return;
        }
        if (ReferenceEquals(job, _current))
        {
            AppendLog("正在运行的任务不能挂起——请用「暂停」（仅下载支持）或「取消当前任务」。");
            return;
        }

        job.IsSuspended = !job.IsSuspended;
        job.StatusDetail = job.IsSuspended ? "已挂起（不参与调度）" : "";
        UpdateJobRow(job);
        AppendLog(job.IsSuspended ? $"已挂起：{job.Display}" : $"已恢复：{job.Display}");
        StartNext();
    }

    private void RemoveSelected()
    {
        if (SelectedJob() is not { } job) return;

        // FR-11：已开跑的任务不可移除——先取消，进终态后再移除。
        if (!job.IsTerminal)
        {
            MessageBox.Show(this,
                "正在运行或排队的任务不能直接移除。\n\n" +
                "「移除」是把任务行从队列摘除且**不写历史**；\n" +
                "「取消」会中断任务并写进历史。先取消，进终态后再移除。",
                "先取消再移除", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _pending.Remove(job);
        _jobs.Remove(job);
        RemoveJobRow(job);
        AppendLog($"已移除（未写历史）：{job.Display}");
    }

    /// <summary>
    /// PRD FR-11：运行中暂停**只有下载支持**。其余类型点了要明确说清"为什么不能"，
    /// 而不是静默无反应——用户会以为按钮坏了。
    /// </summary>
    private void TogglePause()
    {
        if (_current is null) return;

        if (!_current.SupportsPause)
        {
            MessageBox.Show(this,
                $"「{_current.TypeName}」无法中途暂停。\n\n" +
                "下载是唯一在分块之间插了等待点的任务类型；切片（子进程）、" +
                "拼接（一次性 gdal.Warp）、打包（一串一次性步骤）都没有可插等待点的循环。\n\n" +
                "可以先「取消当前任务」，之后从队列重新开始。",
                "该任务类型无法中途暂停", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_current.Pause.IsPaused)
        {
            _current.Pause.Resume();
            _pauseButton.Text = "暂停";
            SetStatus($"{_current.TypeName}：{_current.Display}");
            AppendLog("已恢复");
        }
        else
        {
            _current.Pause.Pause();
            _pauseButton.Text = "继续";
            SetStatus($"已暂停：{_current.Display}");
            AppendLog("已暂停（当前分块写完后停下；恢复后按 Range 续传，不重下）");
        }
    }

    private void CancelCurrent()
    {
        if (_current is null) return;
        AppendLog($"正在取消：{_current.Display}");
        _cancelButton.Enabled = false;
        _current.Cancellation.Cancel();
    }

    // ---------------------------------------------------------------- 历史

    private void ShowHistory()
    {
        using var dialog = new HistoryForm(_history);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.RerunRequest is { } entry) RerunFromHistory(entry);
    }

    /// <summary>
    /// 重跑（FR-09）：不是照着记录直接再跑一遍，而是**打开对应界面并回填**——
    /// 用户点重跑通常是想改一个参数（换个输出目录、换个层级）再跑，
    /// 直接跑等于逼他再填一遍。
    /// </summary>
    private void RerunFromHistory(HistoryEntry entry)
    {
        string? Get(string name)
        {
            try
            {
                using var document = JsonDocument.Parse(entry.ParametersJson);
                return document.RootElement.TryGetProperty(name, out var value) &&
                       value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        int? GetInt(string name)
        {
            try
            {
                using var document = JsonDocument.Parse(entry.ParametersJson);
                return document.RootElement.TryGetProperty(name, out var value) &&
                       value.ValueKind == JsonValueKind.Number
                    ? value.GetInt32()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        double? GetDouble(string name)
        {
            try
            {
                using var document = JsonDocument.Parse(entry.ParametersJson);
                return document.RootElement.TryGetProperty(name, out var value) &&
                       value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        IReadOnlyList<string> GetBoundaryCodes()
        {
            try
            {
                using var document = JsonDocument.Parse(entry.ParametersJson);
                if (!document.RootElement.TryGetProperty("boundaries", out var array) ||
                    array.ValueKind != JsonValueKind.Array)
                    return [];

                // 属性名是 JsonSerializer 按匿名类型的属性名写的，首字母大写。
                return [.. array.EnumerateArray()
                    .Select(item => item.TryGetProperty("Code", out var code) ? code.GetString() : null)
                    .OfType<string>()];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        var kind = Get("kind") ?? "";
        AppendLog($"从历史重跑：{entry.Type}（{entry.StartedAt.ToLocalTime():MM-dd HH:mm}）");

        Job? job = kind switch
        {
            "slice" => BuildSliceJob(Get("input"), Get("output"), GetInt("maxZoom")),
            "mosaic" or "mosaic-vector" => BuildMosaicJob(Get("output"), GetBoundaryCodes()),
            "package" => BuildPackageJob(Get("tiles"), Get("tif"), Get("output")),
            "download" => BuildDownloadJob(
                Get("saveDirectory"), Get("sourceId"),
                GetDouble("west"), GetDouble("south"), GetDouble("east"), GetDouble("north")),
            _ => null,
        };

        if (job is null)
        {
            AppendLog("这条记录的参数无法回填，请手动新建任务。");
            return;
        }
        Enqueue(job);
    }

    private SliceJob? BuildSliceJob(TaskPreset? preset = null)
        => BuildSliceJob(null, null, preset?.MaxZoom, preset);

    private SliceJob? BuildSliceJob(string? input, string? output, int? maxZoom, TaskPreset? preset = null)
    {
        using var dialog = new SliceForm(input, output, maxZoom, preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new SliceJob
        {
            InputPath = dialog.InputPath,
            OutputDirectory = dialog.OutputDirectory,
            MaxZoom = dialog.MaxZoom,
        };
    }

    private MosaicJob? BuildMosaicJob(TaskPreset? preset = null)
        => BuildMosaicJob(null, preset?.BoundaryCodes, preset);

    private MosaicJob? BuildMosaicJob(string? output, IReadOnlyList<string>? boundaryCodes = null,
                                      TaskPreset? preset = null)
    {
        using var dialog = new MosaicForm(
            initialOutput: output ?? PresetOutput(preset),
            initialBoundaryCodes: boundaryCodes,
            preset: preset);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new MosaicJob
        {
            InputPaths = dialog.InputPaths,
            OutputPath = dialog.OutputPath,
            Target = dialog.Target,
            Resampling = dialog.Resampling,
            WriteMetadata = dialog.WriteMetadata,
            Boundaries = dialog.Boundaries,
        };
    }

    private PackageJob? BuildPackageJob(TaskPreset? preset = null)
        => BuildPackageJob(null, null, null, preset);

    private PackageJob? BuildPackageJob(string? tiles, string? tif, string? output,
                                        TaskPreset? preset = null)
    {
        using var dialog = new PackageForm(tiles, tif, output, preset);
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

    private DownloadJob? BuildDownloadJob(
        string? saveDir, string? sourceId,
        double? west, double? south, double? east, double? north)
    {
        var area = west is { } w && south is { } s && east is { } e && north is { } n
            ? new Core.Geo.BBox(w, s, e, n)
            : (Core.Geo.BBox?)null;

        using var dialog = new DownloadForm(area, saveDir, sourceId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        return new DownloadJob
        {
            Source = dialog.SelectedSource,
            Tiles = dialog.Tiles,
            SaveDirectory = dialog.SaveDirectory,
            Area = dialog.Area,
        };
    }

    // ---------------------------------------------------------------- 日志

    private static string CreateLogFile()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"cutmap-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch (Exception)
        {
            return "";
        }
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出日志",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = $"cutmap-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // FR-10：导出的要含**完整命令与输出**，便于交付排障——
            // 只导出界面上这几百行摘要，等于把最关键的那条命令行丢了。
            var header =
                $"{CutMap.Core.CutMapInfo.Name} {CutMap.Core.CutMapInfo.Version}{Environment.NewLine}" +
                $"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"CTB 工具：{_ctbHome.ExePath}{Environment.NewLine}" +
                $"配置目录：{AppPaths.DataDirectory}{Environment.NewLine}" +
                $"操作系统：{Environment.OSVersion}{Environment.NewLine}" +
                $".NET：{Environment.Version}{Environment.NewLine}" +
                new string('=', 70) + Environment.NewLine;

            var tasks = string.Join(Environment.NewLine, _jobs.Select(j =>
                $"  [{j.TypeName}] {j.Display} | {JobStatusText.Of(j.Status)} | {j.StatusDetail}"));

            var body =
                new string('=', 70) + Environment.NewLine +
                "任务清单" + Environment.NewLine +
                new string('-', 70) + Environment.NewLine +
                tasks + Environment.NewLine + Environment.NewLine +
                new string('=', 70) + Environment.NewLine +
                "运行日志" + Environment.NewLine +
                new string('-', 70) + Environment.NewLine +
                _log.Text;

            File.WriteAllText(dialog.FileName, header + body, new System.Text.UTF8Encoding(false));
            AppendLog($"已导出日志：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出日志",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            item.SubItems[2].Text = job.IsSuspended ? "已挂起" : JobStatusText.Of(job.Status);
            item.SubItems[3].Text = job.Progress > 0 ? $"{job.Progress * 100:F0}%" : "";
            item.SubItems[4].Text = job.StatusDetail;
            item.ForeColor = job.IsSuspended ? SystemColors.GrayText : SystemColors.ControlText;
            return;
        }
    }

    private void RemoveJobRow(Job job)
    {
        foreach (ListViewItem item in _jobList.Items)
        {
            if (!ReferenceEquals(item.Tag, job)) continue;
            _jobList.Items.Remove(item);
            return;
        }
    }

    private Job? SelectedJob() =>
        _jobList.SelectedItems.Count > 0 ? _jobList.SelectedItems[0].Tag as Job : null;

    private void UpdateContextMenu()
    {
        var job = SelectedJob();
        _suspendItem.Enabled = job is not null && !job.IsTerminal && !ReferenceEquals(job, _current);
        _suspendItem.Text = job?.IsSuspended == true ? "恢复" : "挂起";
        _removeItem.Enabled = job?.IsTerminal == true;
    }

    /// <summary>预设里没有输出路径（那与具体机器有关），给一个基于预设名的默认值。</summary>
    private static string? PresetOutput(TaskPreset? preset) =>
        preset is null ? null : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CutMap", SanitizeName(preset.Name));

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
        return string.IsNullOrWhiteSpace(cleaned) ? "cutmap" : cleaned;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void AppendLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
        _log.AppendText(stamped);
        if (_pump.Dropped > 0)
            _log.AppendText($"[警告] 有 {_pump.Dropped} 条日志因界面跟不上被丢弃{Environment.NewLine}");

        // 界面只留最近这么多行：一场 22 万瓦片的切片会刷出成百上千行，
        // 全留在 TextBox 里会让界面越用越卡。文件里是全的。
        if (_log.TextLength > 400_000) _log.Text = _log.Text[^200_000..];

        try
        {
            if (_logFilePath.Length > 0) File.AppendAllText(_logFilePath, stamped);
        }
        catch (Exception)
        {
            // 日志写不进去不该影响任务本身。
        }
    }

    /// <summary>下载完成后问一句要不要接着处理这批 DEM，并把分幅预填进拼接对话框。</summary>
    private void OfferContinue(DownloadJob download)
    {
        if (download.DownloadedDirectory is not { } dir) return;

        var files = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.tif").OrderBy(p => p, StringComparer.Ordinal).ToArray()
            : [];
        if (files.Length == 0)
        {
            AppendLog("下载目录里没有 .tif，无法继续处理。");
            return;
        }

        var answer = MessageBox.Show(this,
            $"这批 {files.Length} 幅 DEM 已下载到：\n{dir}\n\n要现在接着做拼接裁剪吗？",
            "继续处理这批 DEM", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        using var dialog = new MosaicForm(files);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Enqueue(new MosaicJob
        {
            InputPaths = dialog.InputPaths,
            OutputPath = dialog.OutputPath,
            Target = dialog.Target,
            Resampling = dialog.Resampling,
            WriteMetadata = dialog.WriteMetadata,
            Boundaries = dialog.Boundaries,
        });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _current?.Cancellation.Cancel();
        _pump.Dispose();
        base.OnFormClosed(e);
    }
}
