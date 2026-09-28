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

    /// <summary>取消用。切片与拼接都是单进程/gdal 内部线程，取消即中断，不涉及进程树。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    public abstract JobResult Execute(
        CtbHome ctbHome,
        Action<double> onProgress,
        Action<string> onLog,
        CancellationToken cancellation);
}
