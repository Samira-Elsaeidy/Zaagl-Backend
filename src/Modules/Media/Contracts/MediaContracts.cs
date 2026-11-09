namespace Zaagl.Modules.Media.Contracts;

// Phase 1 media contracts (part of C2/C9). docs/phase1-implementation/contracts.md, addendum §§6-7.
// Types and interfaces only. EXIF/GPS policy (PD-13), format allowlist (PD-16), SVG handling (PD-18) and
// video transcoding (PD-30) are not approved yet; these types do not decide them.

public enum MediaType : short
{
    Image = 0,
    AnimatedImage = 1,
    Svg = 2,
    Video = 3,
}

/// <summary>Derived previews. Originals are never modified; burn files get no derivatives (PD-05).</summary>
public static class DerivativeKinds
{
    public const string Thumb320 = "thumb-320";
    public const string Preview1600 = "preview-1600";
    public const string Poster = "poster";
    public const string PreviewMp4 = "preview-mp4";
    public const string SvgRaster = "svg-raster";
}

public enum DerivativeStatus : short
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
    Skipped = 3,
}

/// <param name="Url">Ticketed URL on the user-content host (purpose Preview); null until Ready.</param>
public sealed record MediaDerivativeDto(string Kind, DerivativeStatus Status, int? Width, int? Height, string? Url);

public sealed record MediaDto(
    MediaType MediaType,
    string DetectedMime,
    int? Width,
    int? Height,
    int? DurationMs,
    int? FrameCount,
    bool? HasAlpha,
    string? ColorSpace,
    string? IccProfileName,
    bool BrowserPlayable,
    IReadOnlyList<MediaDerivativeDto> Derivatives);

/// <summary>Probe result written by the media worker (NetVips / ffprobe), from the stored original.</summary>
public sealed record MediaProbeResult(
    MediaType MediaType,
    int? Width,
    int? Height,
    int? DurationMs,
    int? FrameCount,
    bool? HasAlpha,
    string? ColorSpace,
    string? IccProfileName,
    bool HasGpsMetadata,
    bool BrowserPlayable);

public interface IMediaProbe
{
    /// <summary>Reads the original by path with time and size limits; never modifies it.</summary>
    Task<MediaProbeResult?> ProbeAsync(Guid resourceId, CancellationToken cancellationToken);
}

public interface IMediaReader
{
    Task<MediaDto?> GetAsync(Guid resourceId, CancellationToken cancellationToken);
}
