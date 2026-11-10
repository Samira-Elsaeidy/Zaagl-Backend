using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>The temp file is shorter than the row says (a crash between the write and the row update cannot cause this; a
/// lost file or a restored volume can). The caller resets the row to <see cref="ActualLength"/>.</summary>
public sealed class TempFileShortException(long actualLength) : IOException("The temp upload file is shorter than the recorded offset.")
{
    public long ActualLength { get; } = actualLength;
}

/// <summary>The body carried more bytes than the session still expects.</summary>
public sealed class UploadBodyTooLargeException() : IOException("The request body is larger than the remaining upload length.");

/// <summary>
/// Files-internal access to <c>temp/</c> (ARC-009 4.2): append at an offset, truncate, read the head, make read-only, delete.
/// Every path comes from <see cref="StorageKeys.Create"/> through <see cref="LocalObjectStorage.ResolvePath"/>; nothing from
/// a request is ever part of a path.
/// </summary>
public sealed class TempUploadFiles(LocalObjectStorage storage)
{
    private const int BufferSize = 81_920;

    public static string KeyFor(Guid id) => StorageKeys.Create(StorageArea.Temp, id);

    private string PathFor(Guid id) => storage.ResolvePath(KeyFor(id));

    public void CreateEmpty(Guid id)
    {
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var _ = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
    }

    public long? Length(Guid id)
    {
        var info = new FileInfo(PathFor(id));
        return info.Exists ? info.Length : null;
    }

    /// <summary>Appends <paramref name="body"/> at <paramref name="offset"/>: the file is first truncated to the row's offset
    /// (a crash between the flush and the row update can leave it longer, ARC-009 4.3), then written and flushed to disk before
    /// the caller updates the row. At most <paramref name="maxBytes"/> are accepted; one more byte is
    /// <see cref="UploadBodyTooLargeException"/> and the file is cut back to <paramref name="offset"/>.
    /// <paramref name="written"/> is updated as bytes are flushed, so a broken connection still records its progress.</summary>
    public async Task AppendAsync(Guid id, long offset, Stream body, long maxBytes, Action<long> written, CancellationToken ct)
    {
        var path = PathFor(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        });
        if (file.Length < offset) throw new TempFileShortException(file.Length);
        if (file.Length > offset) file.SetLength(offset);
        file.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[BufferSize];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maxBytes - total + 1)), ct);
                if (read == 0) break;
                if (total + read > maxBytes)
                {
                    // Cut the file back to the row's offset: nothing of this request counts.
                    await file.FlushAsync(CancellationToken.None);
                    file.SetLength(offset);
                    written(0);
                    throw new UploadBodyTooLargeException();
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
        }
        finally
        {
            // Whatever reached the file before an interruption is flushed and reported: the client resumes from HEAD.
            if (total > 0)
            {
                try
                {
                    file.Flush(flushToDisk: true);
                    written(total);
                }
                catch (IOException) { /* the row keeps the last good offset */ }
            }
        }
    }

    public async Task<byte[]> ReadHeadAsync(Guid id, int maxBytes, CancellationToken ct)
    {
        await using var file = OpenRead(PathFor(id));
        var buffer = new byte[(int)Math.Min(maxBytes, file.Length)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await file.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>The bytes that are hashed, scanned and promoted are the bytes that stopped changing here (ARC-009 4.3).</summary>
    public void MakeReadOnly(Guid id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return;
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct) => storage.DeleteAsync(KeyFor(id), ct);

    private static FileStream OpenRead(string path) => new(path, new FileStreamOptions
    {
        Mode = FileMode.Open,
        Access = FileAccess.Read,
        Share = FileShare.Read | FileShare.Delete,
        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
    });
}
