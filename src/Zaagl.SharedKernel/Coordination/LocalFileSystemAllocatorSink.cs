using System.Text.Json;

namespace Zaagl.SharedKernel.Coordination;

/// <summary>
/// Development stand-in for the real off-host S3-compatible allocator sink (spec §6.4). Simulates
/// write-once semantics with an exclusive-create file open, under a local directory instead of an EU
/// object store. Never use this in production: it lives on the same host as the coordinator, so it
/// gives none of the host-loss protection the real sink exists for.
/// </summary>
public sealed class LocalFileSystemAllocatorSink(string rootDirectory) : IAllocatorSink
{
    private readonly string _manifestPath = Path.Combine(rootDirectory, "manifest.json");
    private readonly string _markersDir = Path.Combine(rootDirectory, "length-started");

    public Task<bool> IsEmptyAsync(CancellationToken ct)
    {
        var empty = !File.Exists(_manifestPath) && (!Directory.Exists(_markersDir) || Directory.GetFiles(_markersDir).Length == 0);
        return Task.FromResult(empty);
    }

    public async Task<bool> TryWriteManifestAsync(InstallationManifest manifest, CancellationToken ct)
    {
        Directory.CreateDirectory(rootDirectory);
        try
        {
            await using var stream = new FileStream(_manifestPath, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct);
            return true;
        }
        catch (IOException)
        {
            return false; // already exists - write-once
        }
    }

    public async Task<InstallationManifest?> ReadManifestAsync(CancellationToken ct)
    {
        if (!File.Exists(_manifestPath)) return null;
        await using var stream = File.OpenRead(_manifestPath);
        return await JsonSerializer.DeserializeAsync<InstallationManifest>(stream, cancellationToken: ct);
    }

    public async Task<bool> TryWriteLengthMarkerAsync(LengthMarker marker, CancellationToken ct)
    {
        Directory.CreateDirectory(_markersDir);
        var path = Path.Combine(_markersDir, $"{marker.PoolId}-{marker.Length}.json");
        if (File.Exists(path))
        {
            var existingJson = await File.ReadAllTextAsync(path, ct);
            var existing = JsonSerializer.Deserialize<LengthMarker>(existingJson);
            // "an identical existing object counts as success" (spec §6.4)
            return existing is not null
                && existing.InstallationId == marker.InstallationId
                && existing.KeyFingerprint == marker.KeyFingerprint
                && existing.PoolId == marker.PoolId
                && existing.Length == marker.Length;
        }
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(stream, marker, cancellationToken: ct);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<LengthMarker>> ListLengthMarkersAsync(string poolId, CancellationToken ct)
    {
        if (!Directory.Exists(_markersDir)) return Task.FromResult<IReadOnlyList<LengthMarker>>([]);
        var markers = new List<LengthMarker>();
        foreach (var file in Directory.GetFiles(_markersDir, $"{poolId}-*.json"))
        {
            var marker = JsonSerializer.Deserialize<LengthMarker>(File.ReadAllText(file));
            if (marker is not null) markers.Add(marker);
        }
        return Task.FromResult<IReadOnlyList<LengthMarker>>(markers);
    }
}
