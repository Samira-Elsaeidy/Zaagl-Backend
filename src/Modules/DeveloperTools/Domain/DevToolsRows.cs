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

/// <summary><c>devtools.hooks</c> (docs/phase2/webhook-catcher.md): a webhook catcher. The limits are a snapshot of the owner's plan at
/// creation, so ingest never reads the plan and a downgrade never changes a live catcher.</summary>
public sealed class HookRow
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    /// <summary>128-bit CSPRNG, base64url (22 chars), case-sensitive (collation "C"). A write-only capability.</summary>
    public string Token { get; set; } = "";
    public string? Label { get; set; }
    public short ResponseStatus { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime PurgeAfter { get; set; }
    public int MaxRequests { get; set; }
    public int MaxBodyBytes { get; set; }
    public long MaxStoredBytes { get; set; }
    public int IngestPerMinute { get; set; }
    public DateTime? WindowStart { get; set; }
    public int WindowCount { get; set; }
    public long ReceivedTotal { get; set; }
    public int StoredCount { get; set; }
    public long StoredBytes { get; set; }
    public DateTime? LastReceivedAt { get; set; }
}

/// <summary><c>devtools.hook_requests</c>: one captured request. Never executed, rendered or served as a document.</summary>
public sealed class HookRequestRow
{
    public long Id { get; set; }
    public Guid HookId { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Query { get; set; } = "";
    /// <summary>JSON array of <c>[name, value]</c> pairs.</summary>
    public string Headers { get; set; } = "[]";
    public string? ContentType { get; set; }
    public byte[]? Body { get; set; }
    public long BodyBytesTotal { get; set; }
    public bool BodyTruncated { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public int StoredBytes { get; set; }
}
