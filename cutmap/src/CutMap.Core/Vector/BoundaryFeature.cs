using CutMap.Core.Geo;

namespace CutMap.Core.Vector;

public enum BoundaryLevel
{
    省,
    市,
    县,
    /// <summary>导入的外部数据，层级不明。</summary>
    其他,
}

/// <summary>
/// 一条行政边界。
///
/// <paramref name="Code"/> 是行政区划代码（数据里的 <c>gb</c> 字段，含 156 前缀），
/// **不是所有要素都有**：内置数据里有若干"境界线"要素（MultiLineString，表示省界/国界
/// 这类不构成面的事物），它们没有 gb 字段，**选择器必须把它们滤掉**——否则用户会选中
/// 一条线去裁剪，得到的结果无法解释（PRD §6 附件明确记了这一条，共 28 个）。
/// </summary>
public sealed record BoundaryFeature(
    string Name,
    string? Code,
    BoundaryLevel Level,
    BBox Bounds,
    string GeometryJson)
{
    /// <summary>是不是可用于裁剪的行政区域。境界线（无编码）不是。</summary>
    public bool IsAdministrative => !string.IsNullOrWhiteSpace(Code);

    /// <summary>
    /// 上级代码。
    ///
    /// gb 是 **9 位 = 3 位国家码 + 6 位区划码**（156 | 500113），省/市/县三级
    /// 长度**完全一样**，所以不能靠长度判断层级，只能用 <see cref="Level"/>
    /// （也就是这条要素来自省/市/县哪个文件）：
    ///
    ///   巴南区 156|500113 → 上级 156|5001|00 = 156500100（市辖区）
    ///   市辖区 156|500100 → 上级 156|50|0000 = 156500000（重庆市）
    ///   重庆市 156|500000 → 无上级
    /// </summary>
    public string? ParentCode => (Level, Code) switch
    {
        (BoundaryLevel.市, { Length: >= 5 } c) => c[..5] + "0000",
        (BoundaryLevel.县, { Length: >= 7 } c) => c[..7] + "00",
        _ => null,
    };

    /// <summary>树里显示的一行。</summary>
    public string Display => $"{Name}（{Code}）";

    public override string ToString() => Display;
}
