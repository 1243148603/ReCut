using CutMap.Core.Geo;

namespace CutMap.Core.Store;

/// <summary>一个裁剪范围预设。M6 会补上用户的增删改与 json 持久化，这里先只有内置项。</summary>
public sealed record ClipPreset(string Id, string Name, BBox Bounds, string Description);

public static class BuiltInPresets
{
    /// <summary>
    /// 重庆全域。范围取自 204 教程 §4.2 的 <c>-te</c>，也是 PRD FR-07 点名要内置的那个；
    /// 它本身来自重庆市外接矩形 (105.2872, 28.1641)–(110.1904, 32.2120) 向内收进一点，
    /// 避开 GLO-30 边缘的低质量区（PRD §6 附件）。
    /// </summary>
    public static readonly ClipPreset Chongqing = new(
        "chongqing",
        "重庆全域",
        new BBox(105.3, 28.1, 110.2, 32.2),
        "105.3 28.1 110.2 32.2（204 教程 §4.2）");

    public static IReadOnlyList<ClipPreset> All => [Chongqing];
}
