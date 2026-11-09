using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>
/// C7 storage keys: <c>&lt;area&gt;/&lt;aa&gt;/&lt;bb&gt;/&lt;uuid&gt;</c>. The only way to make one is <see cref="Create"/>
/// from a server-generated id; nothing from a request (file name, path, MIME type) ever becomes part of a key.
/// The shard <c>aa/bb</c> is the first two bytes of SHA-256 of the id's text (DATABASE_AND_STORAGE R6), so
/// time-ordered ids still spread evenly. Every key read back is re-validated by <see cref="TryParse"/>,
/// including that its shard matches its id, so a crafted key cannot address any other path.
/// </summary>
public static partial class StorageKeys
{
    public static string AreaName(StorageArea area) => area switch
    {
        StorageArea.Temp => "temp",
        StorageArea.Objects => "objects",
        StorageArea.Quarantine => "quarantine",
        StorageArea.Previews => "previews",
        StorageArea.Thumbnails => "thumbnails",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "Unknown storage area."),
    };

    public static string Shard(Guid id)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(id.ToString("D")));
        return $"{hash[0]:x2}/{hash[1]:x2}";
    }

    public static string Create(StorageArea area, Guid id) => $"{AreaName(area)}/{Shard(id)}/{id:D}";

    public static bool TryParse(string? key, out StorageArea area, out Guid id)
    {
        area = default;
        id = default;
        if (key is null || key.Length > 80) return false;
        var m = KeyPattern().Match(key);
        if (!m.Success || !Guid.TryParseExact(m.Groups["id"].Value, "D", out id)) return false;
        area = m.Groups["area"].Value switch
        {
            "temp" => StorageArea.Temp,
            "objects" => StorageArea.Objects,
            "quarantine" => StorageArea.Quarantine,
            "previews" => StorageArea.Previews,
            "thumbnails" => StorageArea.Thumbnails,
            _ => (StorageArea)(-1),
        };
        // The shard must be the one derived from the id: a key is never an arbitrary path.
        return (short)area >= 0 && m.Groups["shard"].Value == Shard(id) && Create(area, id) == key;
    }

    public static (StorageArea Area, Guid Id) Parse(string? key) =>
        TryParse(key, out var area, out var id) ? (area, id) : throw new InvalidStorageKeyException();

    // Lower-case hex only, fixed shape, no dots, no backslashes, no leading slash, anchored.
    [GeneratedRegex(@"\A(?<area>temp|objects|quarantine|previews|thumbnails)/(?<shard>[0-9a-f]{2}/[0-9a-f]{2})/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

public sealed class InvalidStorageKeyException() : ArgumentException("Invalid storage key.");

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Absolute directory holding every area (temp, objects, quarantine, previews, thumbnails). One
    /// filesystem, so a promote is an atomic rename. Empty: <c>%LOCALAPPDATA%/zaagl/storage</c> (development only).</summary>
    public string Root { get; set; } = "";

    /// <summary>Nginx <c>internal</c> location that maps to <see cref="Root"/> (ARC-008).</summary>
    public string InternalPathPrefix { get; set; } = "/_protected/";
}
