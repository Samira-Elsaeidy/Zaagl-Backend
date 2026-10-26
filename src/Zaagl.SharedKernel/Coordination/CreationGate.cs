using System.Net.Http.Json;

namespace Zaagl.SharedKernel.Coordination;

public enum IssuerState { KeyUnverified, KeyMismatch, Waiting, Ok, Faulted }

/// <summary>
/// Gates generated-code AND alias creation on a successful key-fingerprint match with the coordinator
/// (spec §5.4). Until a match, and after any mismatch, both kinds of creation must return 503;
/// everything else (redirects, reads, renewals, deletes) keeps working regardless of this state.
/// A background poll keeps retrying while the coordinator is unreachable or not yet initialised.
/// </summary>
public sealed class CreationGate(HttpClient coordinatorHttp, string localKeyFingerprint)
{
    private volatile IssuerState _state = IssuerState.KeyUnverified;

    public IssuerState State => _state;

    public bool CanCreate => _state == IssuerState.Ok;

    public async Task PollOnceAsync(CancellationToken ct)
    {
        StatusResponse status;
        try
        {
            var response = await coordinatorHttp.GetAsync("/v1/status", ct);
            if (!response.IsSuccessStatusCode)
            {
                _state = IssuerState.Waiting;
                return;
            }
            status = await response.Content.ReadFromJsonAsync<StatusResponse>(ct)
                ?? throw new InvalidOperationException("Empty /v1/status response.");
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            _state = IssuerState.Waiting;
            return;
        }

        if (status.Leasing == "not-initialised")
        {
            _state = IssuerState.Waiting;
            return;
        }

        if (status.KeyFingerprint is null)
        {
            _state = IssuerState.Waiting;
            return;
        }

        _state = string.Equals(status.KeyFingerprint, localKeyFingerprint, StringComparison.OrdinalIgnoreCase)
            ? IssuerState.Ok
            : IssuerState.KeyMismatch;
    }

    public void MarkFaulted() => _state = IssuerState.Faulted;

    private sealed record StatusResponse(string? KeyFingerprint, string Leasing, string? LeasingReason, DateTimeOffset? RestoreDetectedAt);
}
