using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CutMap.Core.Geo;

namespace CutMap.Core.Store;

/// <summary>一个裁剪范围预设（FR-08）。</summary>
public sealed record ClipPreset
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    /// <summary>西 南 东 北。</summary>
    [JsonPropertyName("bounds")] public required double[] Bounds { get; init; }

    [JsonIgnore] public BBox Box => new(Bounds[0], Bounds[1], Bounds[2], Bounds[3]);

    /// <summary>内置预设不可删除（可改名改范围后另存为新预设）。</summary>
    [JsonPropertyName("builtin")] public bool IsBuiltIn { get; init; }

    public override string ToString() => Name;
}

/// <summary>一个切片参数预设（FR-08）。</summary>
public sealed record SlicePreset
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }

    /// <summary>最深层级。null = 按栅格分辨率自动。</summary>
    [JsonPropertyName("max_zoom")] public int? MaxZoom { get; init; }

    [JsonIgnore] public bool IsBuiltIn { get; init; }

    public override string ToString() => Name;
}

/// <summary>
/// 预设持久化（FR-08）。内置的"重庆全域"来自 204 教程 §4.2，其余由用户增删改。
/// 与下载源一样，内置项以代码里的定义为准，不写盘。
/// </summary>
public sealed class PresetStore
{
    private readonly List<ClipPreset> _clip = [];
    private readonly List<SlicePreset> _slice = [];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>内置的重庆全域。范围取自教程 §4.2 的 -te，PRD FR-07 点名要内置。</summary>
    public static ClipPreset Chongqing { get; } = new()
    {
        Id = "chongqing",
        Name = "重庆全域",
        Description = "105.3 28.1 110.2 32.2（204 教程 §4.2）",
        Bounds = [105.3, 28.1, 110.2, 32.2],
        IsBuiltIn = true,
    };

    public static SlicePreset FullPyramid { get; } = new()
    {
        Id = "full-pyramid",
        Name = "完整金字塔（自动层级）",
        MaxZoom = null,
        IsBuiltIn = true,
    };

    public PresetStore()
    {
        _clip.Add(Chongqing);
        _slice.Add(FullPyramid);
        Load();
    }

    public IReadOnlyList<ClipPreset> ClipPresets => _clip;
    public IReadOnlyList<SlicePreset> SlicePresets => _slice;

    public void SaveClip(ClipPreset preset)
    {
        var index = _clip.FindIndex(p => p.Id == preset.Id);
        if (index < 0)
        {
            _clip.Add(preset);
        }
        else
        {
            // 内置预设可编辑，但改出来的结果必须以新 Id 另存——
            // 否则下一次升级换了内置定义，用户改的那份就被覆盖了。
            _clip[index] = preset with { IsBuiltIn = _clip[index].IsBuiltIn };
        }
        Persist();
    }

    public bool DeleteClip(string id)
    {
        var index = _clip.FindIndex(p => p.Id == id);
        if (index < 0 || _clip[index].IsBuiltIn) return false;
        _clip.RemoveAt(index);
        Persist();
        return true;
    }

    public void SaveSlice(SlicePreset preset)
    {
        var index = _slice.FindIndex(p => p.Id == preset.Id);
        if (index < 0) _slice.Add(preset);
        else _slice[index] = preset with { IsBuiltIn = _slice[index].IsBuiltIn };
        Persist();
    }

    public bool DeleteSlice(string id)
    {
        var index = _slice.FindIndex(p => p.Id == id);
        if (index < 0 || _slice[index].IsBuiltIn) return false;
        _slice.RemoveAt(index);
        Persist();
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.UserPresetsFile)) return;
            var loaded = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(AppPaths.UserPresetsFile));
            if (loaded is null) return;

            foreach (var clip in loaded.Clip ?? [])
                if (!clip.IsBuiltIn && _clip.All(p => p.Id != clip.Id)) _clip.Add(clip);
            foreach (var slice in loaded.Slice ?? [])
                if (_slice.All(p => p.Id != slice.Id)) _slice.Add(slice);
        }
        catch (Exception)
        {
            // 预设文件坏了就退回只有内置项，不影响任何正在做的事。
        }
    }

    private void Persist()
    {
        try
        {
            var payload = new Persisted
            {
                Clip = [.. _clip.Where(p => !p.IsBuiltIn)],
                Slice = [.. _slice.Where(p => !p.IsBuiltIn)],
            };
            File.WriteAllText(AppPaths.UserPresetsFile,
                              JsonSerializer.Serialize(payload, WriteOptions),
                              new System.Text.UTF8Encoding(false));
        }
        catch (Exception)
        {
            // 同上：写不进去不该让当前操作失败。
        }
    }

    private sealed record Persisted
    {
        [JsonPropertyName("clip")] public List<ClipPreset>? Clip { get; init; }
        [JsonPropertyName("slice")] public List<SlicePreset>? Slice { get; init; }
    }
}
