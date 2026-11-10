using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>Capacity and free bytes of the filesystem that holds the storage root. A seam so tests can simulate a full disk.</summary>
public interface IDiskSpaceProbe
{
    (long TotalBytes, long FreeBytes) Read(string path);
}

public sealed class DriveDiskSpaceProbe : IDiskSpaceProbe
{
    public (long TotalBytes, long FreeBytes) Read(string path)
    {
        var drive = new DriveInfo(path);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }
}

/// <summary>
/// ARC-009 section 13: before the first byte, refuse a session the disk cannot hold. The free figure is cached for 10 s; the
/// bytes still expected from open sessions are read from the database (a session at 90% done only needs its remainder).
/// <c>507 insufficient-storage</c> is the server's storage, not the user's quota (<c>403 quota-exceeded</c>).
/// </summary>
public sealed class DiskAdmission(
    FilesDbContext db, IDiskSpaceProbe probe, LocalObjectStorage storage, IOptions<StorageOptions> options, TimeProvider time)
{
    private static readonly object Gate = new();
    private static (DateTime At, long Total, long Free, string Root)? _cached;
    private const long SoftCloseSessionCap = 100_000_000;

    /// <summary>Null when the session may be created; otherwise the 507 error.</summary>
    public async Task<Error?> CheckAsync(long declaredLength, CancellationToken ct)
    {
        var o = options.Value.Admission;
        var (total, free) = ReadDisk();
        if (total <= 0) return null; // the probe cannot tell: do not block uploads on a broken probe

        var expected = await db.UploadSessions.AsNoTracking()
            .Where(s => s.Status == Contracts.UploadSessionStatus.Open || s.Status == Contracts.UploadSessionStatus.Complete)
            .SumAsync(s => (long?)(s.DeclaredLength - s.ReceivedLength), ct) ?? 0;

        var usedPercent = 100d * (total - free) / total;
        var refuse = usedPercent >= o.RefuseNewPercent
            || (usedPercent >= o.SoftClosePercent && declaredLength > SoftCloseSessionCap)
            || free - expected - declaredLength < Math.Max(o.MinFreeBytes, total / 100 * o.SafetyPercent);
        return refuse
            ? Error.Of(ErrorCodes.InsufficientStorage, "The server cannot take this upload right now. Try again later.")
            : null;
    }

    public int RetryAfterSeconds => Math.Max(1, options.Value.Admission.RetryAfterSeconds);

    private (long Total, long Free) ReadDisk()
    {
        var now = time.GetUtcNow().UtcDateTime;
        lock (Gate)
        {
            if (_cached is { } c && c.Root == storage.Root && now - c.At < TimeSpan.FromSeconds(10) && probe is DriveDiskSpaceProbe)
                return (c.Total, c.Free);
            var (total, free) = probe.Read(storage.Root);
            if (probe is DriveDiskSpaceProbe) _cached = (now, total, free, storage.Root);
            return (total, free);
        }
    }
}
