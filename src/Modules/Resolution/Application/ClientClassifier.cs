using System.Text.RegularExpressions;
using UAParser;

namespace Zaagl.Modules.Resolution.Application;

/// <param name="Platform">ios | android | desktop, used by device routing rules.</param>
/// <param name="DeviceType">mobile | tablet | desktop | bot, used for analytics.</param>
/// <param name="IsPreviewCrawler">Link-preview fetchers (Slack, WhatsApp, X ...) that should be served Open Graph tags.</param>
public sealed record ClientProfile(
    string? Os,
    string? Browser,
    string Platform,
    string DeviceType,
    bool IsBot,
    bool IsPreviewCrawler);

public sealed partial class ClientClassifier
{
    private readonly Parser _parser = Parser.GetDefault();

    [GeneratedRegex(
        "slackbot|slack-imgproxy|twitterbot|facebookexternalhit|facebot|whatsapp|linkedinbot|discordbot|telegrambot|pinterest|skypeuripreview|redditbot|embedly",
        RegexOptions.IgnoreCase)]
    private static partial Regex PreviewCrawlers();

    [GeneratedRegex(
        "bot\\b|bot/|crawl|spider|slurp|preview|headlesschrome|curl/|wget/|python-requests|go-http-client|libwww|httpclient|monitor|uptime|facebookexternalhit|embedly",
        RegexOptions.IgnoreCase)]
    private static partial Regex Bots();

    public ClientProfile Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new(null, null, "desktop", "bot", true, false);

        var info = _parser.Parse(userAgent);
        var isPreview = PreviewCrawlers().IsMatch(userAgent);
        var isBot = isPreview || info.Device.IsSpider || Bots().IsMatch(userAgent);

        var osFamily = info.OS.Family;
        var platform = osFamily switch
        {
            "iOS" => "ios",
            "Android" => "android",
            _ => "desktop",
        };

        var deviceType = isBot ? "bot"
            : info.Device.Family.Contains("iPad", StringComparison.OrdinalIgnoreCase)
              || info.Device.Family.Contains("Tablet", StringComparison.OrdinalIgnoreCase) ? "tablet"
            : platform is "ios" or "android" ? "mobile"
            : "desktop";

        return new(
            Normalize(osFamily),
            Normalize(info.UA.Family),
            platform,
            deviceType,
            isBot,
            isPreview);
    }

    private static string? Normalize(string? family) =>
        string.IsNullOrWhiteSpace(family) || family == "Other" ? null : family;
}
