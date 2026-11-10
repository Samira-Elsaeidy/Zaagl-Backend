using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Zaagl.Api.Tests.Harness;

/// <summary>What the fake clamd does with the next connection.</summary>
public enum ClamdBehavior
{
    /// <summary>The EICAR test string is FOUND, anything else is OK (the real engine's answers, same wording).</summary>
    Normal,
    /// <summary>Accepts the connection, reads the stream, never answers (a hung engine: the scan times out).</summary>
    Hang,
    /// <summary>Closes the connection before answering.</summary>
    CloseWithoutReply,
    /// <summary>Answers with text that is neither OK nor FOUND.</summary>
    Garbage,
    /// <summary>clamd's StreamMaxLength answer.</summary>
    SizeLimitExceeded,
    /// <summary>Everything is reported infected (a signature that matches any bytes).</summary>
    AlwaysFound,
}

/// <summary>
/// A TCP server that speaks the clamd commands Zaagl uses (<c>zINSTREAM</c>, <c>zSCAN</c>, <c>zPING</c>). The dev machine has
/// no Docker and no clamd, so every scan test runs the real <c>ClamdScanner</c> against this. It verifies the INSTREAM framing
/// (4-byte big-endian length, data, a zero length to end) and records what it received.
/// </summary>
public sealed class FakeClamdServer : IAsyncDisposable
{
    public const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    /// <summary>A stand-in for EICAR in tests that must put the bytes on disk: Windows Defender deletes real EICAR files, so a test
    /// that uploads through the API (which writes temp files) uses this marker. The fake engine finds both the same way.</summary>
    public const string Marker = "ZAAGL-TEST-MALWARE-SIGNATURE-0001";

    private static bool Infected(ReadOnlySpan<byte> data) =>
        Encoding.Latin1.GetString(data).Contains(Eicar, StringComparison.Ordinal) || Encoding.Latin1.GetString(data).Contains(Marker, StringComparison.Ordinal);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private int _connections;
    private long _streamedBytes;
    private int _framingErrors;

    public FakeClamdServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public volatile ClamdBehavior Behavior = ClamdBehavior.Normal;
    public int Connections => Volatile.Read(ref _connections);
    public long StreamedBytes => Interlocked.Read(ref _streamedBytes);
    public int FramingErrors => Volatile.Read(ref _framingErrors);
    public string? LastCommand { get; private set; }
    public string? LastScannedPath { get; private set; }

    /// <summary>Stops listening: connections are refused (clamd down).</summary>
    public void Stop() => _listener.Stop();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _connections);
        using var _ = client;
        await using var stream = client.GetStream();
        try
        {
            var command = await ReadCommandAsync(stream);
            LastCommand = command;
            var behavior = Behavior;
            string reply;
            if (command == "zPING") reply = "PONG";
            else if (command == "zINSTREAM")
            {
                var data = await ReadInstreamAsync(stream);
                reply = behavior switch
                {
                    ClamdBehavior.Normal => Infected(data) ? "stream: Eicar-Test-Signature FOUND" : "stream: OK",
                    ClamdBehavior.AlwaysFound => "stream: Zaagl.Test.Always FOUND",
                    ClamdBehavior.SizeLimitExceeded => "INSTREAM size limit exceeded. ERROR",
                    ClamdBehavior.Garbage => "wat",
                    _ => "stream: OK",
                };
            }
            else if (command.StartsWith("zSCAN ", StringComparison.Ordinal))
            {
                var path = command["zSCAN ".Length..];
                LastScannedPath = path;
                var found = false;
                if (File.Exists(path))
                {
                    await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    found = Infected(ms.ToArray());
                }
                reply = behavior switch
                {
                    ClamdBehavior.Normal => found ? $"{path}: Eicar-Test-Signature FOUND" : $"{path}: OK",
                    ClamdBehavior.AlwaysFound => $"{path}: Zaagl.Test.Always FOUND",
                    ClamdBehavior.Garbage => "wat",
                    _ => $"{path}: OK",
                };
            }
            else reply = "UNKNOWN COMMAND";

            switch (Behavior)
            {
                case ClamdBehavior.Hang:
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                case ClamdBehavior.CloseWithoutReply:
                    return;
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(reply + "\0"));
            await stream.FlushAsync();
        }
        catch (Exception)
        {
            // a dropped client is the scanner's business
        }
    }

    private static async Task<string> ReadCommandAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one) == 1)
        {
            if (one[0] == 0) break;
            bytes.Add(one[0]);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task<byte[]> ReadInstreamAsync(NetworkStream stream)
    {
        using var all = new MemoryStream();
        var header = new byte[4];
        while (true)
        {
            if (!await ReadExactAsync(stream, header, 4)) { Interlocked.Increment(ref _framingErrors); break; }
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 0) break;
            var chunk = new byte[length];
            if (!await ReadExactAsync(stream, chunk, (int)length)) { Interlocked.Increment(ref _framingErrors); break; }
            all.Write(chunk);
            Interlocked.Add(ref _streamedBytes, length);
        }
        return all.ToArray();
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        _stop.Dispose();
    }
}
