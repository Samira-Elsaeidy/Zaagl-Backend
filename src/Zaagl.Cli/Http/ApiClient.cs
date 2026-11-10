using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Http;

/// <summary>The credential to put on a request. <see cref="AccessToken"/> is set for refreshable bearer tokens so the
/// client can tell the token manager which token the server rejected.</summary>
public sealed record AuthHeader(string Scheme, string Value, bool Refreshable, string? AccessToken = null)
{
    public static AuthHeader Bearer(string token, bool refreshable) => new("Bearer", token, refreshable, token);
    public static AuthHeader ApiKey(string key) => new("X-Api-Key", key, false);
}

/// <summary>Supplies credentials (CLI-004/006). <paramref name="rejectedAccessToken"/> is set after a 401.</summary>
public interface ICredentialProvider
{
    Task<AuthHeader> GetAsync(string? rejectedAccessToken, CancellationToken ct);
}

public enum RetryMode
{
    /// <summary>Never resend (e.g. token refresh: a lost answer would burn the refresh token anyway).</summary>
    None,
    /// <summary>Safe to resend: GET, or a POST carrying an Idempotency-Key, or an idempotent POST like logout.</summary>
    Idempotent,
}

/// <summary>
/// CLI-003. One HttpClient over SocketsHttpHandler (system proxy honoured by default). Retries network failures, 5xx,
/// 503 busy, rate limits with a short Retry-After and an in-flight idempotent replay (409 + Retry-After), at most
/// <see cref="MaxRetries"/> times with exponential backoff and jitter; never retries other 4xx. A 401 on a refreshable
/// session triggers one forced refresh and one resend.
/// </summary>
public sealed class ApiClient
{
    public const int MaxRetries = 3;
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ApiClient(HttpClient http, Uri baseAddress, ICredentialProvider? credentials, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        BaseAddress = baseAddress;
        Credentials = credentials;
        _delay = delay;
    }

    public Uri BaseAddress { get; }
    public ICredentialProvider? Credentials { get; }

    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AllowAutoRedirect = false, // never forward a bearer token to wherever a redirect points
        };
        var http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PlatformInfo.UserAgent);
        return http;
    }

    public static string NewIdempotencyKey() => Guid.NewGuid().ToString("N");

    public Uri Resolve(string relative) => new(BaseAddress, relative);

    /// <summary>Sends and returns a successful response, or throws <see cref="ApiException"/> / a network
    /// <see cref="CliException"/>. The factory is called once per attempt (requests cannot be resent).</summary>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> factory, bool authenticate, RetryMode retry, CancellationToken ct)
    {
        string? rejected = null;
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var request = factory();
            AuthHeader? auth = null;
            if (authenticate)
            {
                if (Credentials is null) throw CliException.NotSignedIn();
                auth = await Credentials.GetAsync(rejected, ct).ConfigureAwait(false);
                Apply(request, auth);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransportFailure(ex, ct))
            {
                if (retry == RetryMode.Idempotent && attempt < MaxRetries)
                {
                    await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw ErrorMapper.Network(ex is TaskCanceledException ? "timed out" : "connection failed");
            }

            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && auth is { Refreshable: true } && !refreshed)
                {
                    refreshed = true;
                    rejected = auth.AccessToken;
                    attempt--; // the refresh round does not count as a retry
                    continue;
                }

                var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                if (retry == RetryMode.Idempotent && attempt < MaxRetries && IsTransient(error))
                {
                    await _delay(error.RetryAfter ?? Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                throw error;
            }
        }
    }

    public async Task<T> SendJsonAsync<T>(
        Func<HttpRequestMessage> factory, JsonTypeInfo<T> responseType, bool authenticate, RetryMode retry, CancellationToken ct)
    {
        using var response = await SendAsync(factory, authenticate, retry, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, responseType, ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync(stream, type, ct).ConfigureAwait(false)
                ?? throw new CliException(ExitCodes.Unexpected, "The server sent an empty answer.", "bad-response");
        }
        catch (JsonException)
        {
            throw new CliException(ExitCodes.Unexpected, "The server sent an answer this version of zaagl cannot read.", "bad-response");
        }
    }

    public static HttpRequestMessage Json<T>(HttpMethod method, Uri uri, T body, JsonTypeInfo<T> type, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, type), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (idempotencyKey is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
    }

    public static HttpRequestMessage Get(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void Apply(HttpRequestMessage request, AuthHeader auth)
    {
        if (auth.Scheme == "Bearer")
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Value);
        else
            request.Headers.TryAddWithoutValidation(auth.Scheme, auth.Value);
    }

    /// <summary>Parses an RFC 7807 body (or any JSON with <c>code</c>); non-JSON bodies fall back to the status.</summary>
    public static async Task<ApiException> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var retryAfter = RetryAfterOf(response);
        ProblemDto? problem = null;
        try
        {
            var body = await ReadLimitedAsync(response.Content, 64 * 1024, ct).ConfigureAwait(false);
            if (body.Length > 0 && body.TrimStart().StartsWith('{'))
            {
                problem = JsonSerializer.Deserialize(body, CliJson.Context.ProblemDto);
                if (string.IsNullOrWhiteSpace(problem?.Code) && problem?.Type is null
                    && JsonSerializer.Deserialize(body, CliJson.Context.OAuthErrorDto) is { Error: { Length: > 0 } oauth } oe)
                {
                    // RFC 6749 body of the device token endpoint (C9.2): authorization_pending, slow_down, ...
                    problem = new ProblemDto(null, null, status, oauth, oe.ErrorDescription);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            // Unreadable body: map by status only.
        }
        return ErrorMapper.ToException(problem, status, retryAfter);
    }

    public static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        // HTTP-date form: not converted (would need the clock); treated as absent so the backoff applies.
        return null;
    }

    internal static bool IsTransient(ApiException error) => error.Status switch
    {
        500 or 502 or 503 or 504 => true,
        429 => error.Code is "too-many-requests" or "rate-limit" && error.RetryAfter is { } ra && ra <= MaxRetryAfter,
        409 => error.RetryAfter is { } r && r <= MaxRetryAfter, // idempotent replay still running (spec §7.2)
        _ => false,
    };

    internal static TimeSpan Backoff(int attempt)
    {
        var baseMs = 500 * (1 << Math.Min(attempt, 5));
        return TimeSpan.FromMilliseconds(baseMs + RandomNumberGenerator.GetInt32(0, 250));
    }

    private static bool IsTransportFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static async Task<string> ReadLimitedAsync(HttpContent content, int max, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[max];
        var total = 0;
        int n;
        while (total < max && (n = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct).ConfigureAwait(false)) > 0)
            total += n;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
