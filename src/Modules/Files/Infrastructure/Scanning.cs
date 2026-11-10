using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zaagl.Modules.Files.Infrastructure;

public enum ScanOutcomeKind { Clean, Found, Error }

/// <param name="Signature">Engine signature name when <see cref="ScanOutcomeKind.Found"/>; owner and admin only, never shown to visitors.</param>
/// <param name="Reason">Why an <see cref="ScanOutcomeKind.Error"/> happened (logs and metrics only).</param>
public sealed record ScanResult(ScanOutcomeKind Kind, string Engine, string? Signature = null, string? Reason = null)
{
    public static ScanResult Clean(string engine) => new(ScanOutcomeKind.Clean, engine);
    public static ScanResult Found(string engine, string signature) => new(ScanOutcomeKind.Found, engine, signature);
    public static ScanResult Error(string engine, string reason) => new(ScanOutcomeKind.Error, engine, null, reason);
}

/// <summary>
/// ARC-009 section 10: the malware scan, fail-closed. Only an explicit OK is <see cref="ScanOutcomeKind.Clean"/>; a transport error,
/// a timeout, an unexpected reply or an engine ERROR is <see cref="ScanOutcomeKind.Error"/> (retried, then the upload is
/// rejected as <c>scan-unavailable</c>; the bytes are never served in the meantime).
/// </summary>
public interface IMalwareScanner
{
    /// <param name="content">The temp file, read from the start.</param>
    /// <param name="absolutePath">The same file's path, for the by-path mode (clamd reads it from a shared mount).</param>
    Task<ScanResult> ScanAsync(Stream content, string absolutePath, CancellationToken cancellationToken);
}

/// <summary>clamd over TCP: <c>zINSTREAM</c> by default, <c>zSCAN &lt;path&gt;</c> when <c>ClamAv:ScanByPath</c> is set.</summary>
public sealed class ClamdScanner(IOptions<ClamAvOptions> clam, IOptions<ScanOptions> scan, ILogger<ClamdScanner> logger) : IMalwareScanner
{
    public const string EngineName = "clamd";
    private const int MaxReplyBytes = 4096;

    public async Task<ScanResult> ScanAsync(Stream content, string absolutePath, CancellationToken cancellationToken)
    {
        var o = clam.Value;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, scan.Value.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var ct = linked.Token;
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(o.Host, o.Port, ct);
            await using var stream = client.GetStream();

            string expectedPrefix;
            if (o.ScanByPath)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("zSCAN " + absolutePath + "\0"), ct);
                expectedPrefix = absolutePath;
            }
            else
            {
                await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);
                var chunk = new byte[Math.Clamp(o.StreamChunkBytes, 1024, 1024 * 1024)];
                var header = new byte[4];
                int read;
                while ((read = await content.ReadAsync(chunk, ct)) > 0)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)read);
                    await stream.WriteAsync(header, ct);
                    await stream.WriteAsync(chunk.AsMemory(0, read), ct);
                }
                BinaryPrimitives.WriteUInt32BigEndian(header, 0);
                await stream.WriteAsync(header, ct);
                expectedPrefix = "stream";
            }
            await stream.FlushAsync(ct);

            var reply = await ReadReplyAsync(stream, ct);
            return Parse(reply, expectedPrefix);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("clamd timed out after {Seconds}s", scan.Value.TimeoutSeconds);
            return ScanResult.Error(EngineName, "timeout");
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException)
        {
            logger.LogWarning(ex, "clamd is unreachable or closed the connection");
            return ScanResult.Error(EngineName, "transport: " + ex.GetType().Name);
        }
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxReplyBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (Array.IndexOf(buffer, (byte)0, 0, length) >= 0 || Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0) break;
        }
        return Encoding.UTF8.GetString(buffer, 0, length).Trim('\0', '\n', '\r', ' ');
    }

    /// <summary>Only <c>&lt;prefix&gt;: OK</c> is clean. <c>&lt;prefix&gt;: &lt;signature&gt; FOUND</c> is a detection. Everything else, an
    /// empty reply, <c>INSTREAM size limit exceeded. ERROR</c>, garbage, is an error.</summary>
    public static ScanResult Parse(string reply, string expectedPrefix)
    {
        if (reply.Length == 0) return ScanResult.Error(EngineName, "empty reply");
        if (reply.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var body = reply[..^" FOUND".Length];
            var colon = body.IndexOf(": ", StringComparison.Ordinal);
            var signature = colon >= 0 ? body[(colon + 2)..] : body;
            return ScanResult.Found(EngineName, signature.Length == 0 ? "unknown" : signature);
        }
        if (reply.Equals(expectedPrefix + ": OK", StringComparison.Ordinal)) return ScanResult.Clean(EngineName);
        return ScanResult.Error(EngineName, "unexpected reply: " + (reply.Length > 120 ? reply[..120] : reply));
    }
}

/// <summary>Development only: EICAR is found, the text <c>zaagl-fake-timeout</c> is an engine error, everything else is clean.</summary>
public sealed class FakeScanner : IMalwareScanner
{
    public const string EngineName = "fake";
    public const string TimeoutMarker = "zaagl-fake-timeout";
    public const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    public async Task<ScanResult> ScanAsync(Stream content, string absolutePath, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(content, Encoding.Latin1, false, 81_920, leaveOpen: true);
        var tail = "";
        var chunk = new char[81_920];
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken)) > 0)
        {
            var text = tail + new string(chunk, 0, read);
            if (text.Contains(Eicar, StringComparison.Ordinal)) return ScanResult.Found(EngineName, "Eicar-Test-Signature");
            if (text.Contains(TimeoutMarker, StringComparison.Ordinal)) return ScanResult.Error(EngineName, "timeout");
            tail = text.Length > 128 ? text[^128..] : text;
        }
        return ScanResult.Clean(EngineName);
    }
}
