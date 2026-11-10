using System.Net;
using System.Text;
using Zaagl.Cli;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Commands;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli.Tests;

internal sealed class FakeConsole(byte[]? stdin = null, bool inputRedirected = true) : ICliConsole
{
    public StringWriter OutWriter { get; } = new() { NewLine = "\n" };
    public StringWriter ErrWriter { get; } = new() { NewLine = "\n" };
    public MemoryStream TeeBytes { get; } = new();
    public TextWriter Out => OutWriter;
    public TextWriter Error => ErrWriter;
    public bool IsOutputRedirected => true;
    public bool IsErrorRedirected => true;
    public bool IsInputRedirected => inputRedirected;
    public Stream OpenStandardInput() => new MemoryStream(stdin ?? []);
    public Stream OpenStandardError() => new NonDisposingStream(TeeBytes);
    public string Stdout => OutWriter.ToString();
    public string Stderr => ErrWriter.ToString();

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}

internal sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _items = [];
    public string Name => "memory";
    public int Writes;
    public byte[]? Read(string key) { lock (_items) return _items.GetValueOrDefault(key); }
    public void Write(string key, byte[] secret) { lock (_items) { _items[key] = secret; Writes++; } }
    public bool Delete(string key) { lock (_items) return _items.Remove(key); }
    public int Count => _items.Count;
}

internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeBrowser : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];
    public bool Available { get; set; }
    public bool CanOpen(Func<string, string?> env) => Available;
    public bool TryOpen(Uri url) { Opened.Add(url); return true; }
}

internal sealed record Seen(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);
    public string Path => Uri.AbsolutePath;
}

/// <summary>A fake server: the callback gets each request and returns the response. Requests are recorded.</summary>
internal sealed class FakeServer(Func<Seen, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        var seen = new Seen(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.ToString(), body);
        lock (Requests) Requests.Add(seen);
        return respond(seen);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail = "problem") =>
        new(status) { Content = new StringContent($"{{\"type\":\"https://zaa.gl/problems/{code}\",\"title\":\"t\",\"status\":{(int)status},\"code\":\"{code.Replace('-', '_')}\",\"detail\":\"{detail}\"}}", Encoding.UTF8, "application/problem+json") };
}

internal sealed class TestEnv : IDisposable
{
    public const string Api = "https://api.test.local/";
    public static readonly DateTimeOffset Start = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public TestEnv(Func<Seen, HttpResponseMessage>? respond = null, byte[]? stdin = null, bool inputRedirected = true,
        Dictionary<string, string>? env = null)
    {
        Dir = Path.Combine(Path.GetTempPath(), "zaagl-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Console = new FakeConsole(stdin, inputRedirected);
        Server = new FakeServer(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Env = env ?? [];
        Services = new CliServices
        {
            Console = Console,
            Env = k => Env.GetValueOrDefault(k),
            Store = Store,
            Lock = new FileCrossProcessLock(Dir),
            Browser = Browser,
            ConfigDirectory = Dir,
            Time = Clock,
            Delay = (d, _) => { lock (Delays) Delays.Add(d); return Task.CompletedTask; },
            HttpHandler = () => Server,
        };
    }

    public string Dir { get; }
    public FakeConsole Console { get; }
    public FakeServer Server { get; }
    public Dictionary<string, string> Env { get; }
    public MemoryCredentialStore Store { get; } = new();
    public FakeBrowser Browser { get; } = new();
    public FakeClock Clock { get; } = new(Start);
    public List<TimeSpan> Delays { get; } = [];
    public CliServices Services { get; }

    public string StoreKey => CredentialStores.KeyFor(new Uri(Api));

    public Task<int> Run(params string[] args) =>
        CliApp.RunAsync(["--api-url", Api, .. args], Services, CancellationToken.None);

    public void SignIn(string access = "at-1", string refresh = "rt-1", TimeSpan? accessLife = null)
    {
        var life = accessLife ?? TimeSpan.FromMinutes(15);
        Store.Write(StoreKey, new StoredCredential(refresh, Start.AddDays(30), access, Start, Start + life,
            Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "me@example.com", "Me").Serialize());
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
