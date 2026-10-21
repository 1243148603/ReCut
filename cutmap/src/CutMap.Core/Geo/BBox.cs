using System.Globalization;

namespace CutMap.Core.Geo;

/// <summary>WGS84 经纬度范围。字段顺序是「西 南 东 北」，与 gdalwarp 的 -te 一致。</summary>
public readonly record struct BBox(double West, double South, double East, double North)
{
    public double Width => East - West;
    public double Height => North - South;

    /// <summary>按 -te 的顺序格式化成四个不变文化的小数串。</summary>
    public string[] ToTeArguments() =>
    [
        West.ToString("R", CultureInfo.InvariantCulture),
        South.ToString("R", CultureInfo.InvariantCulture),
        East.ToString("R", CultureInfo.InvariantCulture),
        North.ToString("R", CultureInfo.InvariantCulture),
    ];

    /// <summary>
    /// 经纬度上下限校验。越界必须拒绝：下载侧会按 1° 网格枚举分幅，
    /// 一个越界的经度能枚举出上万幅不存在的分幅，且不会自己报错。
    /// </summary>
    public bool IsValid(out string reason)
    {
        if (!double.IsFinite(West) || !double.IsFinite(South) || !double.IsFinite(East) || !double.IsFinite(North))
        { reason = "范围必须是四个有限数值"; return false; }
        if (West >= East || South >= North)
        {
            reason = "范围为空或反向：需要 西<东 且 南<北";
            return false;
        }
        if (West < -180 || East > 180)
        {
            reason = "经度越界：允许范围 -180 ~ 180";
            return false;
        }
        if (South < -90 || North > 90)
        {
            reason = "纬度越界：允许范围 -90 ~ 90";
            return false;
        }
        reason = "";
        return true;
    }

    public static bool TryParse(string text, out BBox box)
    {
        box = default;
        var parts = text.Trim().Trim('[', ']').Split([' ', ',', '，', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])) return false;
        box = new BBox(numbers[0], numbers[1], numbers[2], numbers[3]);
        return box.IsValid(out _);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"[{West}, {South}, {East}, {North}]");
}
