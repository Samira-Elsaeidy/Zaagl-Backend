using Zaagl.Modules.Resources.Contracts;

namespace Zaagl.Modules.DeveloperTools.Contracts;

// Phase 2 E2.1 (docs/phase2/vault.md, webhook-catcher.md, bug-capsules.md). Wire DTOs of the DeveloperTools module.

/// <summary>Who creates a tool link. <see cref="Via"/> is derived by the Api from the credential, never sent by clients.
/// For a guest, <see cref="TryAdmit"/> consumes one slot of the shared PD-14 guest counters (after the plan checks) and
/// <see cref="Refund"/> gives it back when the creation then fails.</summary>
public sealed record ToolActor(
    Guid? UserId,
    byte[]? GuestCreatorKey,
    CreatedVia Via,
    Func<CancellationToken, Task<bool>>? TryAdmit = null,
    Func<Task>? Refund = null)
{
    public bool IsGuest => UserId is null;
    public ContentOwner Owner => new(UserId, GuestCreatorKey);
}

// ---- Vault -------------------------------------------------------------------------------------------------------

/// <summary>Wire names of the vault's client-side crypto contract (docs/phase2/vault.md).</summary>
public static class VaultWireNames
{
    /// <summary>URL fragment parameter carrying the 256-bit encryption key. NEVER sent to the server.</summary>
    public const string EncryptionKeyFragmentParameter = "e";

    /// <summary>HKDF info string of envelope version 1.</summary>
    public const string HkdfInfoV1 = "zaagl/vault/v1/cek";

    /// <summary>PBKDF2-HMAC-SHA256 iterations for the optional passphrase (version 1).</summary>
    public const int PassphraseIterationsV1 = 600_000;
}

/// <param name="Ciphertext">The version-1 envelope, base64url without padding.</param>
/// <param name="PassphraseProtected">Must match bit 0 of the envelope's flags byte.</param>
/// <param name="MaxViews">1 (default) up to the plan's <c>VaultMaxViews</c>.</param>
/// <param name="Label">Plaintext label for the owner's own list; never shown to viewers. At most 100 characters.</param>
/// <param name="Password">Optional link password (plan capability <c>PasswordOnContent</c>).</param>
public sealed record CreateSecretCommand(
    string? Ciphertext,
    bool PassphraseProtected,
    int? MaxViews,
    DateTime? ExpiresAt,
    string? Label,
    string? Password,
    string? CustomSlug);

/// <param name="LinkKey">The <c>#k=</c> value, returned exactly once. The client appends <c>#k=&lt;LinkKey&gt;&amp;e=&lt;encKey&gt;</c>
/// to <paramref name="ShortUrl"/>.</param>
public sealed record SecretCreatedDto(
    Guid ResourceId,
    string Code,
    string ShortUrl,
    string? LinkKey,
    int MaxViews,
    DateTime ExpiresAt,
    bool IsGuest);

/// <summary>Public descriptor: never the ciphertext. <paramref name="State"/> is <c>available</c> (the only state served
/// with 200).</summary>
public sealed record SecretDescriptorDto(
    string Kind,
    string State,
    int MaxViews,
    int ViewsRemaining,
    DateTime ExpiresAt,
    bool PassphraseProtected,
    bool HasPassword,
    int CiphertextBytes,
    int EnvelopeVersion);

/// <param name="Burned">True when this reveal used the last view (the ciphertext is now wiped).</param>
public sealed record SecretRevealDto(string Ciphertext, int ViewsRemaining, bool Burned, int EnvelopeVersion);

public sealed record SecretEventDto(string Kind, int ViewsUsed, DateTime OccurredAt);

/// <summary>Owner view. <paramref name="State"/>: <c>available</c>, <c>burned</c>, <c>expired</c>, <c>deleted</c>,
/// <c>disabled</c>, <c>taken-down</c>.</summary>
public sealed record OwnerSecretDto(
    Guid Id,
    string? Label,
    string Code,
    string ShortUrl,
    string State,
    int MaxViews,
    int ViewsUsed,
    int CiphertextBytes,
    bool PassphraseProtected,
    bool HasPassword,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastViewedAt,
    DateTime? WipedAt,
    string? WipeReason,
    IReadOnlyList<SecretEventDto>? Events);

// ---- Webhook catcher -----------------------------------------------------------------------------------------------

/// <param name="ResponseStatus">200-299 or 400-599 (1xx and 3xx refused: no redirects from zaa.gl). Default 200.</param>
/// <param name="LifetimeHours">Default and maximum: the plan's <c>HookLifetimeHours</c>.</param>
public sealed record CreateHookCommand(string? Label, int? ResponseStatus, int? LifetimeHours);

public sealed record UpdateHookCommand(string? Label, bool LabelSet, int? ResponseStatus);

/// <summary>Owner view of a catcher. <paramref name="State"/>: <c>live</c> (accepting), <c>read-only</c> (expired, kept until
/// <paramref name="PurgeAfter"/>).</summary>
public sealed record HookDto(
    Guid Id,
    string? Label,
    string IngestUrl,
    string State,
    int ResponseStatus,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime PurgeAfter,
    int MaxRequests,
    int MaxBodyBytes,
    long MaxStoredBytes,
    int IngestPerMinute,
    long ReceivedTotal,
    int StoredCount,
    long StoredBytes,
    DateTime? LastReceivedAt);

/// <summary>A capture without its body (lists and the live stream).</summary>
public sealed record HookRequestSummaryDto(
    long Id,
    DateTime ReceivedAt,
    string Method,
    string Path,
    string Query,
    string? ContentType,
    long BodyBytes,
    bool BodyTruncated,
    string SourceFingerprint);

/// <param name="Headers">Name/value pairs in arrival order.</param>
/// <param name="BodyBase64">The stored body (standard base64), at most the catcher's <c>maxBodyBytes</c>.</param>
/// <param name="BodyText">The same body as text when it is valid UTF-8 without control characters other than tab, CR and LF;
/// otherwise null. Data only: the UI must render it escaped.</param>
public sealed record HookRequestDto(
    long Id,
    DateTime ReceivedAt,
    string Method,
    string Path,
    string Query,
    IReadOnlyList<string[]> Headers,
    string? ContentType,
    long BodyBytes,
    bool BodyTruncated,
    string SourceFingerprint,
    string? BodyBase64,
    string? BodyText);

public sealed record HookRequestPage(IReadOnlyList<HookRequestSummaryDto> Items, long? NextBefore);

// ---- Bug capsules ----------------------------------------------------------------------------------------------------

/// <param name="RecordingResourceId">Optional: one of the caller's ready video uploads (MP4 or WebM).</param>
/// <param name="Logs">The log document, schema version 1 (docs/phase2/bug-capsules.md).</param>
public sealed record CreateCapsuleCommand(
    Guid? RecordingResourceId,
    System.Text.Json.JsonElement Logs,
    string? Title,
    DateTime? ExpiresAt,
    string? Password,
    string? CustomSlug,
    bool Public);

public sealed record CapsuleCreatedDto(Guid ResourceId, string Code, string ShortUrl, string? LinkKey, DateTime ExpiresAt);

/// <param name="State"><c>ready</c>, or <c>unavailable</c> (deleted, expired, taken down): then the URL is null.</param>
public sealed record CapsuleRecordingDto(string State, string? Mime, long? SizeBytes, string? InlineUrl);

public sealed record CapsuleSummaryDto(int ConsoleCount, int ErrorCount, int NetworkCount, int FailedRequestCount, int LogsBytes);

/// <summary>Public descriptor. <paramref name="Logs"/> is data (render escaped, never as markup).</summary>
public sealed record CapsuleDescriptorDto(
    string Kind,
    string? Title,
    DateTime ExpiresAt,
    bool HasPassword,
    CapsuleRecordingDto? Recording,
    CapsuleSummaryDto Summary,
    System.Text.Json.JsonElement Logs);

public sealed record OwnerCapsuleDto(
    Guid Id,
    string Code,
    string ShortUrl,
    string? Title,
    string State,
    DateTime ExpiresAt,
    bool IsPublic,
    bool HasPassword,
    Guid? RecordingResourceId,
    CapsuleRecordingDto? Recording,
    CapsuleSummaryDto Summary,
    DateTime CreatedAt);
