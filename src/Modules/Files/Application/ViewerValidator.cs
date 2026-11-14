using System.Buffers.Binary;
using System.Text.Json;

namespace Zaagl.Modules.Files.Application;

/// <summary>Stored as <c>files.storage_objects.viewer</c> (docs/phase2/viewers-3d-lottie-audio.md).</summary>
public enum ViewerKind : short
{
    Model3d = 1,
    Lottie = 2,
    Audio = 3,
    /// <summary>Screen recordings and other MP4/WebM video (bug capsules, docs/phase2/bug-capsules.md).</summary>
    Video = 4,
}

/// <summary>Configuration section <c>Viewers</c>.</summary>
public sealed class ViewerOptions
{
    public const string Section = "Viewers";

    /// <summary>Largest JSON document parsed (a glTF JSON file, a GLB's JSON chunk, a Lottie file). Larger candidates get no viewer.</summary>
    public int MaxJsonBytes { get; set; } = 16 * 1024 * 1024;
}

/// <param name="Viewer">The viewer for a valid file; null when the file is served as a plain download.</param>
/// <param name="Reject">True when the file must not be accepted at all (external references, Lottie expressions).</param>
/// <param name="KeepType">False when a JSON candidate turned out not to be a Lottie file: the detected type is dropped.</param>
public sealed record ViewerVerdict(ViewerKind? Viewer, bool Reject, bool KeepType, string? Reason)
{
    public static readonly ViewerVerdict None = new(null, false, false, null);
}

/// <summary>
/// Phase 2 E2.2: the full-file check of a viewer candidate found by <see cref="MediaSniffer.DetectViewerType"/>, run by the scan task
/// on the stored bytes. Bounded: at most <see cref="ViewerOptions.MaxJsonBytes"/> of JSON is read and parsed (depth 128). Fail-closed:
/// a 3D or Lottie file that references anything outside itself, or a Lottie file with expressions (JavaScript that players evaluate),
/// is rejected; a malformed candidate gets no viewer.
/// </summary>
public static class ViewerValidator
{
    public const string UnsafeReason = "unsafe-content";
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 128, CommentHandling = JsonCommentHandling.Disallow };

    public static bool IsCandidate(string? mime) =>
        mime is MediaSniffer.GlbMime or MediaSniffer.GltfJsonMime or MediaSniffer.LottieMime
        || (mime is not null && (MediaSniffer.AudioTypes.Contains(mime) || MediaSniffer.VideoTypes.Contains(mime)));

    public static async Task<ViewerVerdict> ValidateAsync(string path, string mime, long size, int maxJsonBytes, CancellationToken ct)
    {
        if (MediaSniffer.AudioTypes.Contains(mime)) return new(ViewerKind.Audio, false, true, null);
        if (MediaSniffer.VideoTypes.Contains(mime)) return new(ViewerKind.Video, false, true, null);
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, Options = FileOptions.Asynchronous,
        });
        return mime switch
        {
            MediaSniffer.GlbMime => await GlbAsync(file, size, maxJsonBytes, ct),
            MediaSniffer.GltfJsonMime => size > maxJsonBytes ? new(null, false, false, "too-large") : Gltf(await ReadAllAsync(file, (int)size, ct)),
            MediaSniffer.LottieMime => size > maxJsonBytes ? new(null, false, false, "too-large") : Lottie(await ReadAllAsync(file, (int)size, ct)),
            _ => ViewerVerdict.None,
        };
    }

    // ---- glTF ------------------------------------------------------------------------------------------------------------------

    private static async Task<ViewerVerdict> GlbAsync(Stream file, long size, int maxJsonBytes, CancellationToken ct)
    {
        var header = new byte[20];
        if (await ReadExactlyAsync(file, header, ct) < 20) return new(null, false, false, "truncated");
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var jsonLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (declared != size) return new(null, false, false, "length-mismatch");
        if (!header.AsSpan(16, 4).SequenceEqual("JSON"u8) || jsonLength == 0 || jsonLength > size - 20) return new(null, false, false, "bad-chunk");
        if (jsonLength > maxJsonBytes) return new(null, false, false, "too-large");
        var json = new byte[jsonLength];
        if (await ReadExactlyAsync(file, json, ct) < json.Length) return new(null, false, false, "truncated");
        return Gltf(json);
    }

    /// <summary>A glTF 2.0 document: <c>asset.version</c> "2.0", and every <c>uri</c> (buffers, images, extensions) a <c>data:</c> URI.</summary>
    public static ViewerVerdict Gltf(ReadOnlyMemory<byte> json)
    {
        using var doc = Parse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return new(null, false, false, "not-json");
        var root = doc.RootElement;
        if (!root.TryGetProperty("asset", out var asset) || asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
            return new(null, false, false, "not-gltf2");
        if (FindExternalUri(root)) return new(null, true, false, UnsafeReason);
        return new(ViewerKind.Model3d, false, true, null);
    }

    private static bool FindExternalUri(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("uri") && (p.Value.ValueKind != JsonValueKind.String || !IsDataUri(p.Value.GetString()))) return true;
                    if (FindExternalUri(p.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) if (FindExternalUri(item)) return true;
                return false;
            default:
                return false;
        }
    }

    // ---- Lottie ----------------------------------------------------------------------------------------------------------------

    /// <summary>A Lottie animation: numeric <c>fr</c>, <c>ip</c>, <c>op</c>, <c>w</c>, <c>h</c> (1..16,384) and an array <c>layers</c>;
    /// image assets embedded; no expressions (an <c>x</c> property holding a string) anywhere.</summary>
    public static ViewerVerdict Lottie(ReadOnlyMemory<byte> json)
    {
        using var doc = Parse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return new(null, false, false, "not-json");
        var root = doc.RootElement;
        foreach (var field in new[] { "fr", "ip", "op", "w", "h" })
            if (!root.TryGetProperty(field, out var v) || v.ValueKind != JsonValueKind.Number) return new(null, false, false, "not-lottie");
        if (!root.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array) return new(null, false, false, "not-lottie");
        foreach (var dim in new[] { "w", "h" })
        {
            if (!root.GetProperty(dim).TryGetDouble(out var d) || d < 1 || d > 16_384) return new(null, false, false, "not-lottie");
        }
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("p", out var p)) continue; // precomps have no "p"
                if (p.ValueKind != JsonValueKind.String || !IsDataUri(p.GetString())) return new(null, true, false, UnsafeReason);
            }
        }
        if (HasExpression(root)) return new(null, true, false, UnsafeReason);
        return new(ViewerKind.Lottie, false, true, null);
    }

    private static bool HasExpression(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("x") && p.Value.ValueKind == JsonValueKind.String) return true;
                    if (HasExpression(p.Value)) return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) if (HasExpression(item)) return true;
                return false;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    private static bool IsDataUri(string? value) => value is not null && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static JsonDocument? Parse(ReadOnlyMemory<byte> json)
    {
        var span = json.Span;
        var start = span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF ? 3 : 0;
        try
        {
            return JsonDocument.Parse(json[start..], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream file, int size, CancellationToken ct)
    {
        var buffer = new byte[size];
        var read = await ReadExactlyAsync(file, buffer, ct);
        return read == size ? buffer : buffer[..read];
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
