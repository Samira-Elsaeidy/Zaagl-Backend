using Zaagl.Modules.Links.Contracts;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Access.Contracts;

// Phase 1 contract C4 (docs/phase1-implementation/contracts.md, ADR-0001 link key, addendum §5).
// Types and interfaces only. The existing Access.Application.AccessGate keeps working for the Phase 0
// redirect until BE work moves Resolution onto IAccessGate.

/// <summary>Wire names shared by the API, the BFF, the viewers, the CLI and the desktop app.</summary>
public static class AccessWireNames
{
    /// <summary>PD-01: the URL fragment parameter, <c>https://zaa.gl/&lt;code&gt;#k=&lt;key&gt;</c>.</summary>
    public const string LinkKeyFragmentParameter = "k";

    /// <summary>Request header that carries the link key from the viewer (via the BFF), CLI or desktop to the API.
    /// The key never appears in a path or query string.</summary>
    public const string LinkKeyHeader = "X-Zaagl-Link-Key";

    /// <summary>Request header that carries an access grant obtained by unlocking a password.</summary>
    public const string GrantHeader = "X-Zaagl-Grant";

    /// <summary>Link key size: 16 CSPRNG bytes = 128 bits.</summary>
    public const int LinkKeyBytes = 16;

    /// <summary>Base64url without padding of <see cref="LinkKeyBytes"/>.</summary>
    public const int LinkKeyEncodedLength = 22;
}

/// <summary>Why the gate is being asked. Purposes never widen: a grant for View does not allow Download.</summary>
public enum AccessPurpose
{
    /// <summary>The <c>zaa.gl/&lt;code&gt;</c> hop. Content kinds: state check only, then 302 to the viewer.</summary>
    Redirect,
    /// <summary>Viewer descriptor / snippet text on <c>api.zaa.gl</c>.</summary>
    View,
    /// <summary>Raw snippet text on the user-content host.</summary>
    Raw,
    /// <summary>Inline preview or thumbnail (never consumes burn).</summary>
    Preview,
    /// <summary>Minting a download ticket (the button POST).</summary>
    Download,
}

public enum AccessGateOutcome
{
    Allowed,
    /// <summary>401 <c>password-required</c>: show the unlock form.</summary>
    PasswordRequired,
    /// <summary>404: no such code, purgeable, missing or wrong link key (indistinguishable on purpose).</summary>
    NotFound,
    /// <summary>410: expired (quarantined) or deleted.</summary>
    Gone,
    /// <summary>410: disabled by the owner or an admin.</summary>
    Disabled,
    /// <summary>410: disabled with kind <c>abuse</c> or <c>safety</c> (acceptable-use page).</summary>
    TakenDown,
}

/// <param name="LinkKey">The presented <c>#k=</c> value, or null.</param>
/// <param name="GrantToken">A token from <see cref="IAccessGrants.UnlockAsync"/>, or null.</param>
public sealed record AccessCredentials(string? LinkKey, string? GrantToken);

/// <param name="LinkId">Set unless the outcome is <see cref="AccessGateOutcome.NotFound"/>.</param>
/// <param name="ResourceId">Set for content kinds.</param>
/// <param name="LinkState">The spec §4.3 link state the gate computed (null for NotFound). Resources needs it to
/// apply the addendum §2.4 order, where <c>burned</c> (a resource fact) ranks above <c>expired</c> (a link fact).</param>
public sealed record AccessGateResult(
    AccessGateOutcome Outcome,
    Guid? LinkId,
    Guid? ResourceId,
    LinkTargetKind? Kind,
    bool IsGuest,
    LinkState? LinkState = null);

/// <summary>
/// C4: the one place link gates are evaluated, used by the redirect and by every content endpoint (the viewer
/// API can be reached without going through <c>zaa.gl</c>). Reads the time from <see cref="TimeProvider"/>.
/// Order: state (spec §4.3) → link key (PD-01) → password / grant. Never consumes clicks, views or burns.
/// </summary>
public interface IAccessGate
{
    Task<AccessGateResult> EvaluateAsync(string code, AccessCredentials credentials, AccessPurpose purpose, CancellationToken cancellationToken);
}

/// <summary>Claims inside a grant token: HMAC-SHA256 over the fields, keyed by a rotating key set like the JWT keys.</summary>
/// <param name="KeyId">The <c>kid</c> of the signing key.</param>
/// <param name="Purposes">The purposes this grant allows; unlock grants View, Raw, Preview and Download.</param>
/// <param name="PasswordStamp">First 4 bytes (big-endian) of SHA-256 of the link's stored password hash at unlock
/// time. The gate refuses the grant when the current stamp differs, so changing or clearing a password revokes
/// every outstanding grant. Wire format: design/ARC-004-access-gate.md.</param>
public sealed record AccessGrantClaims(
    string KeyId,
    Guid LinkId,
    Guid? ResourceId,
    IReadOnlyList<AccessPurpose> Purposes,
    DateTime IssuedAt,
    DateTime ExpiresAt,
    int PasswordStamp);

public sealed record AccessGrant(string Token, DateTime ExpiresAt);

/// <summary>
/// C4: password unlock for content kinds returns a grant instead of a URL. Grants are bound to one link (and
/// resource) and to purposes; they cannot be replayed across links. Default lifetime 10 minutes.
/// </summary>
public interface IAccessGrants
{
    /// <summary>Checks the link key first (wrong or missing key → NotFound), then the password.
    /// Rate-limited by the existing <c>unlock</c> policy.</summary>
    Task<Result<AccessGrant>> UnlockAsync(string code, string password, string? linkKey, CancellationToken cancellationToken);

    /// <summary>Signature, kid, expiry, link binding and purpose. Pure; no I/O.</summary>
    Result<AccessGrantClaims> Validate(string token, Guid linkId, AccessPurpose purpose);
}

/// <summary>A freshly minted link key: the plain value is shown once; only the hash is stored.</summary>
public sealed record NewLinkKey(string PlainKey, byte[] Sha256Hash);

/// <summary>PD-01 key handling. Comparison is constant-time.</summary>
public interface ILinkKeys
{
    NewLinkKey Generate();

    bool Matches(string? presentedKey, byte[] storedSha256Hash);
}
