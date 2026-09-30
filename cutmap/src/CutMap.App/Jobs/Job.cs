using CutMap.Core.Download;
using CutMap.Core.Tools;

namespace CutMap.App.Jobs;

public enum JobStatus
{
    /// <summary>排队中，等前面的跑完。</summary>
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

public static class JobStatusText
{
    public static string Of(JobStatus status) => status switch
    {
        JobStatus.Pending => "等待",
        JobStatus.Running => "运行中",
        JobStatus.Succeeded => "成功",
        JobStatus.Failed => "失败",
        JobStatus.Cancelled => "已取消",
        _ => status.ToString(),
    };
}

/// <param name="Succeeded">任务是否成功。</param>
/// <param name="Summary">成功时的一句话结果，写进队列行与日志。</param>
/// <param name="FailureReason">失败原因，面向用户。</param>
/// <param name="ProductPath">主产物路径，便于后续里程碑（打包、打开目录）取用。</param>
public sealed record JobResult(bool Succeeded, string? Summary, string? FailureReason, string? ProductPath);

/// <summary>
/// 一个排队任务。PRD FR-11 的串行队列元素。
///
/// 让任务自己知道怎么跑（<see cref="Execute"/>），是为了让主窗口不必为每种任务
/// 各写一段调度代码——加任务类型时只加一个子类，不动窗口。
/// </summary>
public abstract class Job
{
    /// <summary>队列里显示的类型名，也是状态栏用的词（PRD FR-11：不显示内部任务号）。</summary>
    public abstract string TypeName { get; }

    /// <summary>队列里那一行的第一列。</summary>
    public abstract string Display { get; }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double Progress { get; set; }
    public string StatusDetail { get; set; } = "";
    public string? FailureReason { get; set; }
    public string? ProductPath { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>
    /// 排队项被"挂起"（PRD FR-11）：挂起的任务不被调度，但仍留在队列里。
    /// 这与"取消"不同——取消进终态并写历史，挂起只是让位。
    /// </summary>
    public bool IsSuspended { get; set; }

    /// <summary>已开跑的任务不可移除：先取消，进终态后再移除（PRD FR-11）。</summary>
    public bool IsTerminal =>
        Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>任务参数的 json，用于历史回填与重跑（FR-09）。由子类提供。</summary>
    public abstract string ParametersJson { get; }

    /// <summary>
    /// 「一键生产」链路的标识：同一批自动串起来的任务共享一个 id。
    ///
    /// 链路里**任一环失败/取消，主窗口会跳过该链剩下的任务**——否则切片会去读
    /// 一个没被产出的 tif、打包再去读不存在的瓦片，一串报错糊到用户脸上，
    /// 而真正的原因只有第一条。
    /// </summary>
    public string? ChainId { get; init; }

    /// <summary>取消用。切片与拼接都是单进程/gdal 内部线程，取消即中断，不涉及进程树。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// 运行中暂停用的等待点。
    ///
    /// **只有下载支持中途暂停**（PRD FR-11 / OUT-07）：下载有一个天然的循环
    /// （1 MB 分块读写），在块之间插等待点即可，恢复后按 Range 续传不重下。
    /// 切片是子进程、拼接是一次性 gdal.Warp、打包是一串一次性步骤，都没有可插
    /// 等待点的循环，点了暂停只能退化为"跑完当前即停"。
    /// </summary>
    public PauseToken Pause { get; } = new();

    /// <summary>该任务类型能否在运行中真正暂停下来。</summary>
    public virtual bool SupportsPause => false;

    public abstract JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation);
}
