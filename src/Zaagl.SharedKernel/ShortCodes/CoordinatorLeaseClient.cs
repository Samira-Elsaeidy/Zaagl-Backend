using System.Net;
using System.Net.Http.Json;

namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// Leases blocks of numbers from the coordinator (spec §6.1 <c>POST /v1/leases</c>) and counts through
/// each block locally, so a code is generated with no network call for all but one request in every
/// block. The coordinator grows the code length over time (spec §4.2); each lease carries the length
/// it was cut from. Thread-safe: concurrent callers share one in-memory block and re-lease together
/// when it runs out.
/// </summary>
public sealed class CoordinatorLeaseClient(HttpClient http, string keyFingerprint, string? nodeId = null, string poolId = "default")
{
    private readonly string _nodeId = nodeId ?? Environment.MachineName;
    private readonly Lock _gate = new();
    private int _length;
    private long _next;
    private long _end; // exclusive upper bound of the current block

    public async Task<(int Length, long Number)> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_next < _end) return (_length, _next++);
            }

            await LeaseAsync(ct);
        }
    }

    private async Task LeaseAsync(CancellationToken ct)
    {
        // Double-checked: another caller may have already leased a fresh block while we were waiting
        // for this method's turn.
        lock (_gate)
        {
            if (_next < _end) return;
        }

        var requestId = Guid.NewGuid();
        var request = new LeaseRequest(requestId, _nodeId, poolId, keyFingerprint);
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("/v1/leases", request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CoordinatorUnavailableException("The coordinator could not be reached: " + ex.Message, ex);
        }

        if (response.StatusCode == HttpStatusCode.InsufficientStorage)
            throw new CoordinatorUnavailableException("The code space is exhausted at the maximum length (10 characters); no more generated codes can be issued.");
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new CoordinatorUnavailableException("The coordinator rejected this lease request: key-mismatch or a reused request id with different data.");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new CoordinatorUnavailableException("The coordinator is not leasing right now (blocked or not initialised).");
        if (!response.IsSuccessStatusCode)
            throw new CoordinatorUnavailableException($"The coordinator answered HTTP {(int)response.StatusCode} to a lease request.");

        var lease = await response.Content.ReadFromJsonAsync<LeaseResponse>(ct)
            ?? throw new InvalidOperationException("Coordinator returned an empty lease.");

        lock (_gate)
        {
            if (_next >= _end) // still exhausted; nobody else refreshed it meanwhile
            {
                _length = lease.CodeLength;
                _next = lease.StartNumber;
                _end = lease.EndNumber;
            }
        }
    }

    private sealed record LeaseRequest(Guid RequestId, string NodeId, string PoolId, string KeyFingerprint);

    private sealed record LeaseResponse(long LeaseId, string PoolId, int CodeLength, short KeyVersion, long StartNumber, long EndNumber);
}

/// <summary>
/// The coordinator cannot hand out a block of numbers right now (down, not initialised, blocked, key mismatch or code space
/// exhausted). It is an operational condition, not a caller mistake: the API answers a clean 503 with Retry-After and never echoes
/// this message (it names internals). Still an <see cref="InvalidOperationException"/> so existing catch blocks keep working.
/// </summary>
public sealed class CoordinatorUnavailableException : InvalidOperationException
{
    public CoordinatorUnavailableException(string message) : base(message) { }
    public CoordinatorUnavailableException(string message, Exception inner) : base(message, inner) { }
}
