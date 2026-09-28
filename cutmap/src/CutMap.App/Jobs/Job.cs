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

/// <summary>一次切片。PRD FR-11 的队列模型，M1 阶段只有切片一种任务。</summary>
public sealed class SliceJob
{
    public required string InputPath { get; init; }
    public required string OutputDirectory { get; init; }
    public int? MaxZoom { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Pending;
    public double Progress { get; set; }
    public string StatusDetail { get; set; } = "";
    public string? FailureReason { get; set; }
    public string? CommandLine { get; set; }
    public TimeSpan Elapsed { get; set; }

    /// <summary>取消用。切片是单进程多线程，Kill 进程即可，不需要处理进程树。</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>队列里显示的一行。</summary>
    public string Display => $"{Path.GetFileName(InputPath)} → {OutputDirectory}";
}
