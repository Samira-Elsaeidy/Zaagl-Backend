namespace Zaagl.SharedKernel.Coordination;

public sealed record InstallationManifest(string InstallationId, string KeyFingerprint, string PoolId, int InitialLength, DateTimeOffset CreatedAt);

public sealed record LengthMarker(string InstallationId, string KeyFingerprint, string PoolId, int Length, DateTimeOffset At);

/// <summary>
/// The off-host evidence a coordinator needs to recover after losing its entire host (spec §6.4): a
/// write-once installation manifest, and a write-once marker per code length it has ever started.
/// Production needs a real S3-compatible bucket in the EU with versioning and object lock
/// (<c>ALLOC_SINK_CREDENTIALS</c>); this build ships only <see cref="LocalFileSystemAllocatorSink"/>
/// for development, selected by <c>Allocator:Sink:Type = "LocalFileSystem"</c>. A real S3
/// implementation is not built in this phase - see the coordinator's README note.
/// </summary>
public interface IAllocatorSink
{
    /// <summary>True when neither a manifest nor any length marker exists yet (a precondition for <c>init</c>).</summary>
    Task<bool> IsEmptyAsync(CancellationToken ct);

    /// <summary>Write-once: returns false (without error) if a manifest already exists.</summary>
    Task<bool> TryWriteManifestAsync(InstallationManifest manifest, CancellationToken ct);

    Task<InstallationManifest?> ReadManifestAsync(CancellationToken ct);

    /// <summary>Write-once: returns true if the marker was written or an identical one already existed (spec: "an identical existing object counts as success").</summary>
    Task<bool> TryWriteLengthMarkerAsync(LengthMarker marker, CancellationToken ct);

    /// <summary>Every length marker ever written for this pool, in no particular order.</summary>
    Task<IReadOnlyList<LengthMarker>> ListLengthMarkersAsync(string poolId, CancellationToken ct);
}
