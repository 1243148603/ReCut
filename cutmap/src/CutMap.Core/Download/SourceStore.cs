using System.Text.Encodings.Web;
using System.Text.Json;
using CutMap.Core.Store;

namespace CutMap.Core.Download;

/// <summary>
/// 下载源列表的持久化（FR-16）。
///
/// 内置源**始终存在且不可删除**（可禁用）：删掉它之后如果用户的镜像全挂了，
/// 就没有任何可用的源，而"下不到数据"的原因会变得难以判断。用户能改的只是
/// 启不启用它。
/// </summary>
public sealed class SourceStore
{
    private readonly List<DownloadSource> _sources = [];
    private readonly string _filePath;
    private List<DownloadSource> _lastSaved = [];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SourceStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.SourcesFile;
        _sources.Add(DownloadSource.CopernicusOfficial);
        _sources.Add(DownloadSource.CopernicusEu);
        Load();
        _lastSaved = [.. _sources];
    }

    public IReadOnlyList<DownloadSource> All => _sources;

    /// <summary>下拉里可选的源。</summary>
    public IReadOnlyList<DownloadSource> Enabled =>
        [.. _sources.Where(s => s.Enabled)];

    public DownloadSource? ById(string id) => _sources.FirstOrDefault(s => s.Id == id);

    /// <summary>新增或替换。内置源的 Id 与 IsBuiltIn 会被强制保留。</summary>
    public void Save(DownloadSource source)
    {
        var existing = _sources.FindIndex(s => s.Id == source.Id);
        if (existing < 0)
        {
            _sources.Add(source);
        }
        else
        {
            var isBuiltIn = _sources[existing].IsBuiltIn;
            _sources[existing] = source with
            {
                IsBuiltIn = isBuiltIn,
                Id = _sources[existing].Id,
            };
        }
        Persist();
    }

    /// <summary>删除。内置源删不掉，返回 false。</summary>
    public bool Delete(string id)
    {
        var index = _sources.FindIndex(s => s.Id == id);
        if (index < 0 || _sources[index].IsBuiltIn) return false;
        _sources.RemoveAt(index);
        Persist();
        return true;
    }

    /// <summary>记一次成败，便于判断内网镜像还活着没有（FR-16 最后一条）。</summary>
    public void RecordOutcome(string id, bool success, string? error)
    {
        var index = _sources.FindIndex(s => s.Id == id);
        if (index < 0) return;
        _sources[index] = success
            ? _sources[index] with { LastSuccess = DateTimeOffset.Now, LastError = null }
            : _sources[index] with { LastError = error };
        Persist();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<List<DownloadSource>>(
                File.ReadAllText(_filePath));
            if (loaded is null) return;

            foreach (var source in loaded)
            {
                var builtin = _sources.FindIndex(s => s.IsBuiltIn && s.Id == source.Id);
                if (builtin >= 0)
                {
                    // 模板由代码维护，用户开关和最近成败记录由配置维护。
                    _sources[builtin] = _sources[builtin] with
                    {
                        Enabled = source.Enabled,
                        LastSuccess = source.LastSuccess,
                        LastError = source.LastError,
                    };
                    continue;
                }
                if (source.IsBuiltIn) continue;
                if (_sources.Any(s => s.Id == source.Id)) continue;
                _sources.Add(source);
            }
        }
        catch (Exception)
        {
            // 配置坏了不该让软件起不来：内置源已经在列表里，用户重新加一条即可。
        }
    }

    private void Persist()
    {
        try
        {
            AtomicFile.Write(_filePath, JsonSerializer.Serialize(_sources, WriteOptions));
            _lastSaved = [.. _sources];
        }
        catch
        {
            _sources.Clear();
            _sources.AddRange(_lastSaved);
            throw;
        }
    }
}
