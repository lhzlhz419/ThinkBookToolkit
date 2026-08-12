using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThinkBookToolkit;

internal sealed record ApplicationRelease(
    Version Version,
    string TagName,
    Uri PageUri);

internal static class ApplicationUpdateService
{
    private const string LatestReleaseEndpoint =
        "https://api.github.com/repos/lhzlhz419/ThinkBookToolkit/releases/latest";
    public static Version CurrentVersion { get; } =
        typeof(ApplicationUpdateService).Assembly.GetName().Version ??
        new Version(0, 0, 0, 0);

    public static string CurrentVersionText => FormatVersion(CurrentVersion);
    private static readonly HttpClient Client = CreateClient();

    public static async Task<ApplicationRelease> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            LatestReleaseEndpoint,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseReleaseJson(json);
    }

    internal static ApplicationRelease ParseReleaseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString();
        var pageUrl = root.GetProperty("html_url").GetString();
        if (string.IsNullOrWhiteSpace(tagName) ||
            string.IsNullOrWhiteSpace(pageUrl))
        {
            throw new InvalidOperationException(
                "GitHub 返回的发布信息不完整。");
        }

        var versionText = tagName.Trim();
        if (versionText.StartsWith('v') || versionText.StartsWith('V'))
            versionText = versionText[1..];
        var suffix = versionText.IndexOfAny(['-', '+']);
        if (suffix >= 0)
            versionText = versionText[..suffix];
        if (!Version.TryParse(versionText, out var version))
        {
            throw new InvalidOperationException(
                $"无法识别 GitHub Release 版本号“{tagName}”。");
        }

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri) ||
            !string.Equals(pageUri.Scheme, Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pageUri.Host, "github.com",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "GitHub 返回了无效的发布页面地址。");
        }

        return new ApplicationRelease(version, tagName, pageUri);
    }

    internal static bool IsNewer(ApplicationRelease release) =>
        Normalize(release.Version) > Normalize(CurrentVersion);

    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build),
        Math.Max(0, version.Revision));

    private static string FormatVersion(Version version) =>
        $"{Math.Max(0, version.Major)}.{Math.Max(0, version.Minor)}." +
        $"{Math.Max(0, version.Build)}";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(
                "ThinkBookToolkit",
                CurrentVersionText));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add(
            "X-GitHub-Api-Version",
            "2022-11-28");
        return client;
    }
}
