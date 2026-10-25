using System.Net;
using System.Net.Http.Json;

namespace Zaagl.SharedKernel.ShortCodes;

/// <summary>
/// Leases blocks of numbers from the coordinator service and counts through each block locally, so a
/// code is generated with no network call for all but one request in every block. The coordinator
/// grows the code length over time (spec §4.2); each lease carries the length it was cut from, and a
/// fresh lease can be a different (always equal-or-greater) length than the block before it.
/// Thread-safe: concurrent callers share one in-memory block and re-lease together when it runs out.
/// </summary>
public sealed class CoordinatorLeaseClient(HttpClient http)
{
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

        var response = await http.PostAsync("/lease", content: null, ct);
        if (response.StatusCode == HttpStatusCode.InsufficientStorage)
        {
            throw new InvalidOperationException(
                "The code space is exhausted at the maximum length (10 characters); no more generated codes can be issued.");
        }
        response.EnsureSuccessStatusCode();
        var lease = await response.Content.ReadFromJsonAsync<LeaseResponse>(ct)
            ?? throw new InvalidOperationException("Coordinator returned an empty lease.");

        lock (_gate)
        {
            if (_next >= _end) // still exhausted; nobody else refreshed it meanwhile
            {
                _length = lease.Length;
                _next = lease.Start;
                _end = lease.Start + lease.Size;
            }
        }
    }

    private sealed record LeaseResponse(int Length, long Start, int Size);
}
