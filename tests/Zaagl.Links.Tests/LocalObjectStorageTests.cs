using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Zaagl.Modules.Files.Contracts;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Links.Tests;

/// <summary>BE-004 / C7: keys are generated and validated (no traversal), writes are atomic, moves are atomic
/// renames, range reads, idempotent delete, X-Accel internal paths only for deliverable areas.</summary>
public sealed class LocalObjectStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zaagl-storage-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalObjectStorage _storage;

    public LocalObjectStorageTests() =>
        _storage = new LocalObjectStorage(Options.Create(new StorageOptions { Root = _root }));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Bytes(int n) => RandomNumberGenerator.GetBytes(n);

    private async Task<PutResult> PutAsync(byte[] data, StorageArea area = StorageArea.Temp) =>
        await _storage.PutAsync(area, new MemoryStream(data), default);

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        await using (s)
        {
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    [Fact]
    public async Task Put_writes_the_bytes_returns_size_and_sha256_and_a_generated_sharded_key()
    {
        var data = Bytes(200_000);
        var put = await PutAsync(data);

        Assert.Equal(data.Length, put.SizeBytes);
        Assert.Equal(SHA256.HashData(data), put.Sha256);
        Assert.Matches(@"^temp/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}$", put.StorageKey);
        var (area, id) = StorageKeys.Parse(put.StorageKey);
        Assert.Equal(StorageArea.Temp, area);
        Assert.Equal(StorageKeys.Shard(id), put.StorageKey[5..10]); // shard from the id's hash, not its prefix
        Assert.Equal(data, await ReadAllAsync(await _storage.OpenReadAsync(put.StorageKey, 0, null, default)));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.partial", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("objects/../../etc/passwd")]
    [InlineData("/objects/ab/cd/0b5c4b4e-6a39-4c3a-9b52-1f0d3b8a2c11")]
    [InlineData("objects\\ab\\cd\\0b5c4b4e-6a39-4c3a-9b52-1f0d3b8a2c11")]
    [InlineData("objects/AB/CD/0B5C4B4E-6A39-4C3A-9B52-1F0D3B8A2C11")]
    [InlineData("objects/00/00/0b5c4b4e-6a39-4c3a-9b52-1f0d3b8a2c11")] // well-formed, but not this id's shard
    [InlineData("other/ab/cd/0b5c4b4e-6a39-4c3a-9b52-1f0d3b8a2c11")]
    [InlineData("objects/ab/cd/0b5c4b4e-6a39-4c3a-9b52-1f0d3b8a2c11/x")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("")]
    public async Task Crafted_keys_are_rejected_everywhere(string key)
    {
        Assert.False(StorageKeys.TryParse(key, out _, out _));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => _storage.OpenReadAsync(key, 0, null, default));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => _storage.StatAsync(key, default));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => _storage.DeleteAsync(key, default));
        await Assert.ThrowsAsync<InvalidStorageKeyException>(() => _storage.MoveAsync(key, StorageArea.Objects, default));
        Assert.Throws<InvalidStorageKeyException>(() => _storage.GetInternalPath(key));
    }

    [Fact]
    public async Task Range_reads_return_exactly_the_requested_bytes()
    {
        var data = Bytes(10_000);
        var key = (await PutAsync(data)).StorageKey;

        Assert.Equal(data[1000..1500], await ReadAllAsync(await _storage.OpenReadAsync(key, 1000, 500, default)));
        Assert.Equal(data[9_990..], await ReadAllAsync(await _storage.OpenReadAsync(key, 9_990, 500, default))); // clipped at end
        Assert.Equal(data[5000..], await ReadAllAsync(await _storage.OpenReadAsync(key, 5000, null, default)));
        Assert.Empty(await ReadAllAsync(await _storage.OpenReadAsync(key, 10_000, null, default)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _storage.OpenReadAsync(key, 10_001, null, default));
    }

    [Fact]
    public async Task Promote_is_a_rename_and_concurrent_promotes_all_return_the_same_key()
    {
        var data = Bytes(50_000);
        var temp = (await PutAsync(data)).StorageKey;

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => _storage.MoveAsync(temp, StorageArea.Objects, default))));

        var promoted = Assert.Single(results.Distinct());
        Assert.StartsWith("objects/", promoted);
        Assert.Equal(temp[4..], promoted["objects".Length..]); // same shard and id, different area
        Assert.Null(await _storage.StatAsync(temp, default));
        Assert.Equal(data.Length, await _storage.StatAsync(promoted, default));
        Assert.Equal(data, await ReadAllAsync(await _storage.OpenReadAsync(promoted, 0, null, default)));
    }

    [Fact]
    public async Task Moving_a_missing_object_fails()
    {
        var key = StorageKeys.Create(StorageArea.Temp, Guid.NewGuid());
        await Assert.ThrowsAnyAsync<IOException>(() => _storage.MoveAsync(key, StorageArea.Objects, default));
    }

    [Fact]
    public async Task Delete_is_idempotent_and_stat_reports_missing_as_null()
    {
        var key = (await PutAsync(Bytes(10))).StorageKey;
        await _storage.DeleteAsync(key, default);
        await _storage.DeleteAsync(key, default);
        await _storage.DeleteAsync(StorageKeys.Create(StorageArea.Objects, Guid.NewGuid()), default); // never existed
        Assert.Null(await _storage.StatAsync(key, default));
    }

    [Fact]
    public void Internal_paths_exist_only_for_deliverable_areas()
    {
        var id = Guid.NewGuid();
        var objects = StorageKeys.Create(StorageArea.Objects, id);
        Assert.Matches(@"^/_protected/(objects|previews|thumbnails)/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f-]{36}$", _storage.GetInternalPath(objects));
        Assert.Equal("/_protected/" + objects, _storage.GetInternalPath(objects));
        Assert.Throws<InvalidOperationException>(() => _storage.GetInternalPath(StorageKeys.Create(StorageArea.Temp, id)));
        Assert.Throws<InvalidOperationException>(() => _storage.GetInternalPath(StorageKeys.Create(StorageArea.Quarantine, id)));
    }

    [Fact]
    public async Task A_failed_write_leaves_no_object_and_no_partial_file()
    {
        await Assert.ThrowsAsync<IOException>(() => _storage.PutAsync(StorageArea.Temp, new FailingStream(), default));
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task List_returns_old_keys_of_one_area()
    {
        var a = (await PutAsync(Bytes(5))).StorageKey;
        var b = (await PutAsync(Bytes(5), StorageArea.Objects)).StorageKey;
        var listed = new List<string>();
        await foreach (var k in _storage.ListAsync(StorageArea.Temp, DateTime.UtcNow.AddMinutes(1), default)) listed.Add(k);
        Assert.Equal([a], listed);
        listed.Clear();
        await foreach (var k in _storage.ListAsync(StorageArea.Temp, DateTime.UtcNow.AddHours(-1), default)) listed.Add(k);
        Assert.Empty(listed);
        Assert.NotNull(b);
    }

    [Fact]
    public void A_relative_root_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => new LocalObjectStorage(Options.Create(new StorageOptions { Root = "relative/dir" })));

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            _reads++ == 0 ? Fill(buffer, offset, count) : throw new IOException("client went away");
        private static int Fill(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)1, offset, count); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
