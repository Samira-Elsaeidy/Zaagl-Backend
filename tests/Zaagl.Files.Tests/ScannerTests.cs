using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Zaagl.Api.Tests.Harness;
using Zaagl.Modules.Files.Infrastructure;

namespace Zaagl.Files.Tests;

/// <summary>ARC-009 section 10: the real <see cref="ClamdScanner"/> against a fake clamd. Only an explicit OK is clean (QA-025).</summary>
public sealed class ScannerTests : IAsyncLifetime
{
    private FakeClamdServer _clamd = null!;

    public Task InitializeAsync()
    {
        _clamd = new FakeClamdServer();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _clamd.DisposeAsync();

    private ClamdScanner Scanner(bool byPath = false, int timeoutSeconds = 5) => new(
        Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = _clamd.Port, ScanByPath = byPath, StreamChunkBytes = 4096 }),
        Options.Create(new ScanOptions { TimeoutSeconds = timeoutSeconds }),
        NullLogger<ClamdScanner>.Instance);

    private static (Stream Stream, string Path) TempFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "zaagl-scan-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, content);
        return (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose), path);
    }

    [Fact]
    public async Task A_clean_file_is_clean_and_is_streamed_in_valid_instream_frames()
    {
        var content = new byte[100_000];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content);
        var (stream, path) = TempFile(content);
        await using var _ = stream;
        var result = await Scanner().ScanAsync(stream, path, CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Clean, result.Kind);
        Assert.Equal("zINSTREAM", _clamd.LastCommand);
        Assert.Equal(content.Length, _clamd.StreamedBytes);
        Assert.Equal(0, _clamd.FramingErrors);
    }

    [Fact]
    public async Task Eicar_is_found_and_the_signature_is_returned()
    {
        // In memory only: Windows Defender removes real EICAR files from disk.
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("prefix " + FakeClamdServer.Eicar + " suffix"));
        var result = await Scanner().ScanAsync(stream, "unused", CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Found, result.Kind);
        Assert.Equal("Eicar-Test-Signature", result.Signature);
    }

    [Theory]
    [InlineData(ClamdBehavior.Garbage)]
    [InlineData(ClamdBehavior.SizeLimitExceeded)]
    [InlineData(ClamdBehavior.CloseWithoutReply)]
    public async Task Anything_but_an_explicit_ok_is_an_error_never_clean(ClamdBehavior behavior)
    {
        _clamd.Behavior = behavior;
        var (stream, path) = TempFile("harmless"u8.ToArray());
        await using var _ = stream;
        var result = await Scanner().ScanAsync(stream, path, CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Error, result.Kind);
    }

    [Fact]
    public async Task A_hung_engine_times_out_as_an_error()
    {
        _clamd.Behavior = ClamdBehavior.Hang;
        var (stream, path) = TempFile("harmless"u8.ToArray());
        await using var _ = stream;
        var result = await Scanner(timeoutSeconds: 1).ScanAsync(stream, path, CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Error, result.Kind);
        Assert.Equal("timeout", result.Reason);
    }

    [Fact]
    public async Task A_stopped_engine_is_an_error()
    {
        _clamd.Stop();
        var (stream, path) = TempFile("harmless"u8.ToArray());
        await using var _ = stream;
        var result = await Scanner().ScanAsync(stream, path, CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Error, result.Kind);
    }

    [Fact]
    public async Task Scan_by_path_sends_zscan_with_the_file_path()
    {
        var (stream, path) = TempFile(Encoding.ASCII.GetBytes("zz " + FakeClamdServer.Marker));
        await using var _ = stream;
        var result = await Scanner(byPath: true).ScanAsync(stream, path, CancellationToken.None);

        Assert.Equal(ScanOutcomeKind.Found, result.Kind);
        Assert.Equal(path, _clamd.LastScannedPath);
        Assert.Equal(0, _clamd.StreamedBytes);
    }

    [Theory]
    [InlineData("stream: OK", "stream", ScanOutcomeKind.Clean)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", "stream", ScanOutcomeKind.Found)]
    [InlineData("stream: OKAY", "stream", ScanOutcomeKind.Error)]
    [InlineData("OK", "stream", ScanOutcomeKind.Error)]
    [InlineData("stream: OK ERROR", "stream", ScanOutcomeKind.Error)]
    [InlineData("", "stream", ScanOutcomeKind.Error)]
    [InlineData("/tmp/a: OK", "/tmp/b", ScanOutcomeKind.Error)]
    public void The_reply_parser_accepts_only_the_exact_ok(string reply, string prefix, ScanOutcomeKind expected) =>
        Assert.Equal(expected, ClamdScanner.Parse(reply, prefix).Kind);

    [Fact]
    public async Task The_fake_scanner_used_in_development_finds_eicar_and_errors_on_its_marker()
    {
        var scanner = new FakeScanner();
        Assert.Equal(ScanOutcomeKind.Found, (await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(FakeScanner.Eicar)), "", default)).Kind);
        Assert.Equal(ScanOutcomeKind.Error, (await scanner.ScanAsync(new MemoryStream("xx zaagl-fake-timeout xx"u8.ToArray()), "", default)).Kind);
        Assert.Equal(ScanOutcomeKind.Clean, (await scanner.ScanAsync(new MemoryStream("fine"u8.ToArray()), "", default)).Kind);
    }
}
