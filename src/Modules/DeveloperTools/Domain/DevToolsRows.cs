namespace Zaagl.Modules.DeveloperTools.Domain;

/// <summary><c>devtools.secrets</c> (docs/phase2/vault.md): one row per vault resource. Holds ONLY the client's ciphertext
/// envelope and non-secret metadata; the encryption key never reaches the server. <see cref="Ciphertext"/> is null once wiped
/// (burned, deleted, expired, disabled, purged). The resource id is the Resources row's id (no cross-schema foreign key).</summary>
public sealed class SecretRow
{
    public Guid ResourceId { get; set; }
    /// <summary>Copy of the owner (null for guests), for the owner list only.</summary>
    public Guid? OwnerId { get; set; }
    public byte[]? Ciphertext { get; set; }
    public int CiphertextBytes { get; set; }
    public short EnvelopeVersion { get; set; }
    public bool PassphraseProtected { get; set; }
    public short MaxViews { get; set; }
    public short ViewsUsed { get; set; }
    /// <summary>Copy of the link's expiry (kept current from C10 LinkRenewed); read by the wipe sweep only, never for state.</summary>
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastViewedAt { get; set; }
    public DateTime? WipedAt { get; set; }
    public string? WipeReason { get; set; }
}

/// <summary><c>devtools.secret_events</c>: the audit trail of a secret, without contents, IP or user agent.</summary>
public sealed class SecretEventRow
{
    public long Id { get; set; }
    public Guid ResourceId { get; set; }
    public string Kind { get; set; } = "";
    public short ViewsUsed { get; set; }
    public DateTime OccurredAt { get; set; }
}

public static class SecretEventKinds
{
    public const string Created = "created";
    public const string Revealed = "revealed";
    public const string Burned = "burned";
    public const string Deleted = "deleted";
    public const string Expired = "expired";
    public const string Disabled = "disabled";
    public const string Purged = "purged";
}

public static class WipeReasons
{
    public const string Burned = "burned";
    public const string Deleted = "deleted";
    public const string Expired = "expired";
    public const string Disabled = "disabled";
    public const string Purged = "purged";
}
