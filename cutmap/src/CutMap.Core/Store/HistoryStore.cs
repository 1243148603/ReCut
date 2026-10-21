using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CutMap.Core.Store;

/// <summary>
/// 一条任务历史（FR-09）。
///
/// <paramref name="ParametersJson"/> 存**能原样回填对话框**的任务参数，而不是
/// 一句人读的摘要：FR-09 要的是"双击记录打开对应界面并回填"，重跑时不必重填。
/// 所以这里存的是参数对象本身，摘要只是给人看的旁支。
/// </summary>
public sealed record HistoryEntry
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>任务类型名（切片 / 拼接裁剪 / 矢量裁剪 / 打包 / 下载）。</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("started_at")]
    public required DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("elapsed_seconds")]
    public double ElapsedSeconds { get; init; }

    /// <summary>成功 / 失败 / 已取消。</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "";

    [JsonPropertyName("product_path")]
    public string? ProductPath { get; init; }

    /// <summary>失败原因，便于事后判断是不是同一类问题。</summary>
    [JsonPropertyName("failure")]
    public string? Failure { get; init; }

    /// <summary>任务参数，用于回填与重跑。</summary>
    [JsonPropertyName("parameters")]
    public string ParametersJson { get; init; } = "{}";

    [JsonPropertyName("log_path")]
    public string? LogPath { get; init; }
}

/// <summary>
/// 历史持久化（FR-09）。落 <c>%APPDATA%\CutMap\history.json</c>。
///
/// 上限 500 条：这是个本机操作记录，不是审计日志，无限增长只会让文件越来越慢，
/// 而超出这个量的旧记录实际没人会翻。
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 500;

    private readonly List<HistoryEntry> _entries = [];
    private readonly string _filePath;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public HistoryStore(string? filePath = null) { _filePath = filePath ?? AppPaths.HistoryFile; Load(); }

    /// <summary>最近的在最前。</summary>
    public IReadOnlyList<HistoryEntry> All => [.. _entries.OrderByDescending(e => e.StartedAt)];

    public void Add(HistoryEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
        {
            var oldest = _entries.MinBy(e => e.StartedAt)!;
            _entries.Remove(oldest);
        }
        Persist();
    }

    /// <summary>删除单条（FR-09）。</summary>
    public bool Delete(string id)
    {
        var removed = _entries.RemoveAll(e => e.Id == id) > 0;
        if (removed) Persist();
        return removed;
    }

    /// <summary>
    /// 清空全部。**调用方必须先做二次确认**（FR-09 明确要求"先确认后清除"）——
    /// 这里不做确认，因为 Core 层弹不了对话框，而写一个"这里本该确认"的注释
    /// 不如把责任放在唯一能确认的地方。
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        Persist();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(
                File.ReadAllText(_filePath));
            if (loaded is not null) _entries.AddRange(loaded);
        }
        catch (Exception)
        {
            // 历史坏了不该让软件起不来：这只是记录，丢了不影响任何正在做的事。
        }
    }

    private void Persist()
    {
        AtomicFile.Write(_filePath, JsonSerializer.Serialize(_entries, WriteOptions));
    }
}
