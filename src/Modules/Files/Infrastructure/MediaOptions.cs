namespace Zaagl.Modules.Files.Infrastructure;

/// <summary><c>Media:*</c> (ARC-011 section 6): decoding cost bounds for the image probe and the thumbnail worker.</summary>
public sealed class MediaOptions
{
    public const string Section = "Media";

    /// <summary>Header-declared pixel count (width x height) above which no derivative is made and the original is only offered for
    /// download ("skipped"). Decided from the header before any decoder exists. ARC-011 section 6: 100,000,000.</summary>
    public long MaxPixels { get; set; } = 100_000_000;

    /// <summary>Decoded pixels the thumbnailer allows itself in memory (4 bytes each). Skia has no streaming access, so this is
    /// lower than <see cref="MaxPixels"/> (about 160 MB at the default); JPEG is decoded at a reduced scale and is only bound by the
    /// scaled size. Above it the derivative is "skipped".</summary>
    public long MaxDecodePixels { get; set; } = 40_000_000;

    /// <summary>Probed pixel count above which the original is not offered inline (descriptor <c>inlineUrl</c> null, owner inline
    /// ticket refused): "Download original" only. ARC-011 section 6 (interim): 100,000,000.</summary>
    public long MaxInlinePixels { get; set; } = 100_000_000;

    /// <summary>The longest side of <c>thumb-320</c>.</summary>
    public int ThumbnailEdge { get; set; } = 320;

    public int WebpQuality { get; set; } = 80;

    /// <summary>Images processed at once in this process (<c>Media:MaxParallel</c>, ARC-011 section 6).</summary>
    public int MaxParallel { get; set; } = 2;

    /// <summary>Per-image wall-clock limit for probe plus thumbnail.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Task attempts for infrastructure failures (database, disk). Image-level failures never retry: they end as
    /// "no thumbnail".</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Rows the backfill sweep enqueues per run.</summary>
    public int BackfillBatch { get; set; } = 200;
}
