using System.Text.Json;

namespace Zaagl.Coordinator;

public sealed record PoolHwm(int Length, long ReservedOffset, DateTimeOffset WrittenAt);

public sealed record HwmFile(
    int Format,
    string? InstallationId,
    string? KeyFingerprint,
    string State, // "initializing" | "active"
    Dictionary<string, PoolHwm> Pools);

/// <summary>
/// The coordinator's one piece of durable local evidence (spec §6.4 "HWM file"): written only inside
/// the serialised section, via temp-file-then-atomic-rename so a crash mid-write never leaves a
/// corrupt file. The new value for any pool must be >= its current value - callers are responsible for
/// only ever calling <see cref="WriteAsync"/> with a state that is ahead of or equal to the last write.
/// </summary>
public sealed class HwmStore(string path)
{
    public async Task<HwmFile?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<HwmFile>(json);
    }

    public async Task WriteAsync(HwmFile file, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, file, cancellationToken: ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true); // fsync the file
        }
        File.Move(tmp, path, overwrite: true);
        // fsync the directory so the rename itself is durable (best-effort: .NET has no portable
        // directory-fsync API; on Windows dev this is a documented gap, not attempted).
    }
}
