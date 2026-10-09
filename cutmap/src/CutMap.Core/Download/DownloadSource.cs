using System.Text.Json.Serialization;

namespace CutMap.Core.Download;

/// <summary>
/// 一个下载源（FR-16）。**地址不写死**：内网镜像、自建对象存储、官方 AWS 都用同一种
/// "URL 模板"描述，软件只负责按模板拼串、续传、校验。
///
/// 占位符口径**照 PRD FR-16**，与 204 教程 §3.1 的写法不同：
///
///   {name}  分幅标签全名，如 N29_E106
///   {lat}   含半球字母的纬度标签，如 N29      ← 教程里这一位是纯数字 29
///   {lon}   含半球字母的经度标签，如 E106
///
/// 也就是说官方的
/// <c>Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM</c>
/// 展开成 <c>Copernicus_DSM_COG_10_N29_00_E106_00_DEM</c>，正是教程那条 URL。
/// **谁要是把教程的 <c>N{lat}_00_E{lon}_00</c> 原样粘进模板，会得到 "N N29"** ——
/// 所以界面上必须显示展开后的 URL 预览。
/// </summary>
public sealed record DownloadSource
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>下拉里显示的名字。</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("url_template")]
    public required string UrlTemplate { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>内置源不可删除（可禁用）。</summary>
    [JsonPropertyName("builtin")]
    public bool IsBuiltIn { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("last_success")]
    public DateTimeOffset? LastSuccess { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>Copernicus GLO-30 官方源。教程 §3.1 已验证。</summary>
    public const string CopernicusTemplate =
        "https://copernicus-dem-30m.s3.amazonaws.com/" +
        "Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM/" +
        "Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM.tif";

    public static DownloadSource CopernicusOfficial { get; } = new()
    {
        Id = "copernicus-official",
        Name = "Copernicus GLO-30 官方源（AWS）",
        UrlTemplate = CopernicusTemplate,
        Description = "免注册直下，单幅约 45 MB",
        IsBuiltIn = true,
    };

    /// <summary>
    /// 同一个官方桶的**欧洲边缘节点**：数据、目录结构、文件大小与主源完全一致，
    /// 只是走不同的接入点。国内路由到两个节点的质量会来回变——一个慢/抽风时
    /// 换另一个。两个都是内置的官方源，不存在"静默换了别的数据源"（FR-16）。
    /// </summary>
    public const string CopernicusTemplateEu =
        "https://copernicus-dem-30m.s3.eu-central-1.amazonaws.com/" +
        "Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM/" +
        "Copernicus_DSM_COG_10_{lat}_00_{lon}_00_DEM.tif";

    public static DownloadSource CopernicusEu { get; } = new()
    {
        Id = "copernicus-eu",
        Name = "Copernicus GLO-30 官方源（AWS 欧洲节点）",
        UrlTemplate = CopernicusTemplateEu,
        Description = "同一个官方桶的欧洲接入点，与主源互为主备",
        IsBuiltIn = true,
    };

    /// <summary>把模板按分幅展开成真实 URL。</summary>
    public string Expand(DemTile tile) =>
        UrlTemplate
            .Replace("{name}", tile.Name, StringComparison.Ordinal)
            .Replace("{lat}", tile.LatLabel, StringComparison.Ordinal)
            .Replace("{lon}", tile.LonLabel, StringComparison.Ordinal);

    /// <summary>
    /// 模板是否可用。**不检查地址可达**——那是下载时才该知道的事，
    /// 这里只挡"拼不出合法 URL"这种明显错误。
    /// </summary>
    public bool IsUsable(out string reason)
    {
        if (string.IsNullOrWhiteSpace(UrlTemplate))
        {
            reason = "URL 模板为空。";
            return false;
        }
        if (!UrlTemplate.Contains("{lat}", StringComparison.Ordinal) &&
            !UrlTemplate.Contains("{lon}", StringComparison.Ordinal) &&
            !UrlTemplate.Contains("{name}", StringComparison.Ordinal))
        {
            reason = "模板里至少要有一个占位符（{name} / {lat} / {lon}），否则所有分幅会指向同一个地址。";
            return false;
        }

        var probe = Expand(new DemTile(29, 106, "N29", "E106", new Geo.BBox(106, 29, 107, 30)));
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            reason = $"模板展开后不是合法的 http(s) 地址：{probe}";
            return false;
        }

        reason = "";
        return true;
    }
}
