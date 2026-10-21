using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zaagl.Modules.Links.Application;

public sealed record ReputationResult(bool IsSafe, string? Threat = null);

public interface IUrlReputationChecker
{
    Task<ReputationResult> CheckAsync(string url, CancellationToken cancellationToken);
}

public sealed class WebRiskOptions
{
    public const string Section = "WebRisk";

    /// <summary>Google Web Risk API key. When empty, scanning is skipped.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Google Web Risk lookup. Fails open (treats the URL as safe) when the service is unreachable
/// so an outage cannot stop link creation; the failure is logged.
/// </summary>
public sealed class WebRiskChecker(HttpClient http, IOptions<WebRiskOptions> options, ILogger<WebRiskChecker> logger)
    : IUrlReputationChecker
{
    private const string Endpoint = "https://webrisk.googleapis.com/v1/uris:search";
    private readonly string _apiKey = options.Value.ApiKey;

    public async Task<ReputationResult> CheckAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return new(true);

        var query = "threatTypes=MALWARE&threatTypes=SOCIAL_ENGINEERING&threatTypes=UNWANTED_SOFTWARE"
            + $"&uri={Uri.EscapeDataString(url)}&key={Uri.EscapeDataString(_apiKey)}";
        try
        {
            using var response = await http.GetAsync($"{Endpoint}?{query}", cancellationToken);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

            if (doc.RootElement.TryGetProperty("threat", out var threat)
                && threat.TryGetProperty("threatTypes", out var types)
                && types.GetArrayLength() > 0)
            {
                return new(false, types[0].GetString());
            }
            return new(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Web Risk lookup failed; allowing URL");
            return new(true);
        }
    }
}
