using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Files.Tests;

/// <summary>ARC-009 4.3: append at an offset, truncate to the row, keep what was flushed, read-only once complete.</summary>
public sealed class TempUploadFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zaagl-temp-" + Guid.NewGuid().ToString("N"));
    private readonly TempUploadFiles _temp;
    private readonly LocalObjectStorage _storage;

    public TempUploadFilesTests()
    {
        _storage = new LocalObjectStorage(Options.Create(new StorageOptions { Root = _root }));
        _temp = new TempUploadFiles(_storage);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Two_appends_make_one_file_in_the_layout_of_the_storage_keys()
    {
        var id = Guid.CreateVersion7();
        long written = 0;
        await _temp.AppendAsync(id, 0, new MemoryStream("hello "u8.ToArray()), 100, w => written = w, default);
        Assert.Equal(6, written);
        await _temp.AppendAsync(id, 6, new MemoryStream("world"u8.ToArray()), 100, w => written = w, default);

        Assert.Equal(11, _temp.Length(id));
        Assert.Equal("hello world", System.Text.Encoding.ASCII.GetString(await _temp.ReadHeadAsync(id, 64, default)));
        Assert.True(File.Exists(Path.Combine(_root, TempUploadFiles.KeyFor(id).Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task A_file_longer_than_the_row_is_cut_back_to_the_row_offset_before_appending()
    {
        // A crash between the flush and the row update leaves extra bytes at the end.
        var id = Guid.CreateVersion7();
        long written = 0;
        await _temp.AppendAsync(id, 0, new MemoryStream("0123456789"u8.ToArray()), 100, w => written = w, default);

        await _temp.AppendAsync(id, 4, new MemoryStream("ABC"u8.ToArray()), 100, w => written = w, default);

        Assert.Equal(7, _temp.Length(id));
        Assert.Equal("0123ABC", System.Text.Encoding.ASCII.GetString(await _temp.ReadHeadAsync(id, 64, default)));
    }

    [Fact]
    public async Task A_file_shorter_than_the_row_is_reported_with_its_real_length()
    {
        var id = Guid.CreateVersion7();
        long written = 0;
        await _temp.AppendAsync(id, 0, new MemoryStream("abc"u8.ToArray()), 100, w => written = w, default);

        var ex = await Assert.ThrowsAsync<TempFileShortException>(() =>
            _temp.AppendAsync(id, 10, new MemoryStream("x"u8.ToArray()), 100, w => written = w, default));
        Assert.Equal(3, ex.ActualLength);
    }

    [Fact]
    public async Task A_body_past_the_allowed_bytes_is_refused_and_nothing_of_it_stays()
    {
        var id = Guid.CreateVersion7();
        long written = 0;
        await _temp.AppendAsync(id, 0, new MemoryStream("abc"u8.ToArray()), 100, w => written = w, default);

        await Assert.ThrowsAsync<UploadBodyTooLargeException>(() =>
            _temp.AppendAsync(id, 3, new MemoryStream(new byte[50]), maxBytes: 10, w => written = w, default));
        Assert.Equal(3, _temp.Length(id));
        Assert.Equal(0, written);
    }

    [Fact]
    public async Task An_interrupted_body_keeps_the_bytes_that_were_flushed_so_the_client_can_resume()
    {
        var id = Guid.CreateVersion7();
        long written = 0;
        var broken = new BreakingStream(new byte[300_000], breakAfter: 200_000);

        await Assert.ThrowsAsync<IOException>(() => _temp.AppendAsync(id, 0, broken, 1_000_000, w => written = w, default));

        Assert.Equal(200_000, written);
        Assert.Equal(200_000, _temp.Length(id));
    }

    [Fact]
    public async Task A_completed_file_is_read_only_and_can_still_be_deleted()
    {
        var id = Guid.CreateVersion7();
        long written = 0;
        await _temp.AppendAsync(id, 0, new MemoryStream("done"u8.ToArray()), 100, w => written = w, default);
        _temp.MakeReadOnly(id);

        var path = _storage.ResolvePath(TempUploadFiles.KeyFor(id));
        Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
        await Assert.ThrowsAnyAsync<Exception>(() => _temp.AppendAsync(id, 4, new MemoryStream("more"u8.ToArray()), 100, w => written = w, default));

        await _temp.DeleteAsync(id, default);
        Assert.Null(_temp.Length(id));
    }

    private sealed class BreakingStream(byte[] data, int breakAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= breakAfter) throw new IOException("connection reset");
            var n = Math.Min(Math.Min(buffer.Length, data.Length - _position), breakAfter - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            await Task.Yield();
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
