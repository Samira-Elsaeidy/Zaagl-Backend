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
