using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Contracts;

namespace Zaagl.Modules.Files.Infrastructure;

/// <summary>
/// C7 / BE-004 (PD-15): object storage on one local filesystem under <see cref="StorageOptions.Root"/>.
/// <list type="bullet">
/// <item>Writes stream to a <c>.partial</c> file next to the final path (same directory, same volume), hashing
/// while copying, flushed to disk, then renamed into place; a reader never sees a half-written object.</item>
/// <item><see cref="MoveAsync"/> (temp → objects, objects → quarantine) is one atomic rename; concurrent moves of
/// the same key all return the same target key and exactly one rename happens.</item>
/// <item>Every key is validated (<see cref="StorageKeys"/>) and the resolved path must stay under the root.</item>
/// </list>
/// </summary>
public sealed class LocalObjectStorage : IObjectStorage
{
    private const int BufferSize = 81_920;
    private const string PartialSuffix = ".partial";
    private static readonly StorageArea[] DeliverableAreas = [StorageArea.Objects, StorageArea.Previews, StorageArea.Thumbnails];

    private readonly string _root;
    private readonly string _internalPrefix;

    public LocalObjectStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        var root = string.IsNullOrWhiteSpace(o.Root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "zaagl", "storage")
            : o.Root;
        if (!Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Storage:Root must be an absolute path.");
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Directory.CreateDirectory(_root);
        _internalPrefix = "/" + o.InternalPathPrefix.Trim('/') + "/";
    }

    public string Root => _root;

    public async Task<PutResult> PutAsync(StorageArea area, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = StorageKeys.Create(area, Guid.NewGuid());
        var finalPath = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        var partialPath = finalPath + PartialSuffix;

        long size = 0;
        byte[] sha256;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partialPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0,
            }))
            {
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    size += read;
                }
                file.Flush(flushToDisk: true);
            }
            sha256 = hash.GetHashAndReset();
            File.Move(partialPath, finalPath, overwrite: false);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        return new PutResult(key, size, sha256);
    }

    public Task<string> MoveAsync(string storageKey, StorageArea target, CancellationToken cancellationToken)
    {
        var (area, id) = StorageKeys.Parse(storageKey);
        var targetKey = StorageKeys.Create(target, id);
        if (area == target) return Task.FromResult(targetKey);

        var source = PathFor(storageKey);
        var destination = PathFor(targetKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            File.Move(source, destination, overwrite: false); // rename(2) on one filesystem: atomic
        }
        catch (IOException) when (!File.Exists(source) && File.Exists(destination))
        {
            // Another caller already moved it (concurrent promote): same outcome, nothing to do.
        }
        return Task.FromResult(targetKey);
    }

    public Task<Stream> OpenReadAsync(string storageKey, long offset, long? length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length));

        var file = new FileStream(PathFor(storageKey), new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            // Delete: a concurrent delete/move must not fail because a download is in progress.
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });
        try
        {
            if (offset > file.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset is past the end of the object.");
            file.Seek(offset, SeekOrigin.Begin);
            var available = file.Length - offset;
            return Task.FromResult<Stream>(length is { } l && l < available ? new BoundedReadStream(file, l) : file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public Task<long?> StatAsync(string storageKey, CancellationToken cancellationToken)
    {
        var info = new FileInfo(PathFor(storageKey));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = PathFor(storageKey);
        TryDelete(path + PartialSuffix);
        try
        {
            File.Delete(path); // missing file: no exception
        }
        catch (DirectoryNotFoundException)
        {
            // never written: already "deleted"
        }
        return Task.CompletedTask;
    }

    public string GetInternalPath(string storageKey)
    {
        var (area, _) = StorageKeys.Parse(storageKey);
        if (Array.IndexOf(DeliverableAreas, area) < 0)
            throw new InvalidOperationException($"Objects in '{StorageKeys.AreaName(area)}' are never delivered.");
        return _internalPrefix + storageKey;
    }

    /// <summary>Keys in <paramref name="area"/> last written before <paramref name="olderThanUtc"/> (orphan sweeps,
    /// expired temp uploads). Partial files are listed too, as their final key, so a crashed write is swept.</summary>
    public async IAsyncEnumerable<string> ListAsync(
        StorageArea area, DateTime olderThanUtc, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dir = Path.Combine(_root, StorageKeys.AreaName(area));
        if (!Directory.Exists(dir)) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.EndsWith(PartialSuffix, StringComparison.Ordinal)) relative = relative[..^PartialSuffix.Length];
            if (!StorageKeys.TryParse(relative, out _, out _) || !seen.Add(relative)) continue;
            if (File.GetLastWriteTimeUtc(path) < olderThanUtc) yield return relative;
            await Task.Yield();
        }
    }

    /// <summary>The only place a key becomes a path: validated, then checked to stay under the root.</summary>
    private string PathFor(string storageKey)
    {
        StorageKeys.Parse(storageKey);
        var full = Path.GetFullPath(Path.Combine(_root, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidStorageKeyException();
        return full;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Read-only view of the next <c>length</c> bytes of a stream (range reads).</summary>
    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
