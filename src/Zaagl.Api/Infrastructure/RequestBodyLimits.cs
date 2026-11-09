using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// BE-005 (contracts.md C8, ops-001 "Body size"): Kestrel's global request-body limit stays small (Nginx allows 2 MB on
/// api.zaa.gl outside /api/uploads); an endpoint that needs more raises it for itself only with
/// <see cref="RequestBodyLimitExtensions.WithRequestBodyLimit"/> (tus PATCH and /api/uploads/simple will use
/// UploadProtocol.MaxChunkBytes, 90,000,000, with BE-011). Slow-body protection keeps Kestrel's minimum data rate.
/// </summary>
public sealed class RequestBodyLimitOptions
{
    public const string Section = "Limits";

    /// <summary>Default for every endpoint: 2 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Kestrel MinRequestBodyDataRate: bytes per second after the grace period (Kestrel default 240).</summary>
    public double MinRequestBodyBytesPerSecond { get; set; } = 240;

    public int MinRequestBodyGraceSeconds { get; set; } = 5;

    public void Apply(KestrelServerLimits limits)
    {
        limits.MaxRequestBodySize = MaxRequestBodyBytes;
        limits.MinRequestBodyDataRate = new MinDataRate(MinRequestBodyBytesPerSecond, TimeSpan.FromSeconds(MinRequestBodyGraceSeconds));
    }
}

public static class RequestBodyLimitExtensions
{
    /// <summary>Per-endpoint body limit (IRequestSizeLimitMetadata, applied by endpoint routing before the body is read).</summary>
    public static TBuilder WithRequestBodyLimit<TBuilder>(this TBuilder builder, long maxBytes) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RequestSizeLimitAttribute(maxBytes));
}
