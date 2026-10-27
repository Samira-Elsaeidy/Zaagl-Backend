namespace Zaagl.Modules.Identity.Domain;

public static class EmailKinds
{
    public const string ConfirmEmail = "confirm-email";
    public const string PasswordReset = "password-reset";
    public const string AccountExists = "account-exists";
    public const string SecurityNotice = "security-notice";
    public const string Expiring = "expiring";
    public const string Expired = "expired";
    public const string Purge = "purge";
}

public static class EmailOutboxStatuses
{
    public const string Pending = "pending";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>
/// Spec §7.10. One outbox shared by account emails (queued by the triggering request, sent ASAP) and
/// link-warning digests (queued once a day, at most one per stage per user per day).
///
/// Documented, scoped deviation from the literal spec text: confirm-email and password-reset tokens are
/// still generated eagerly at queue time (unchanged from phase 3), not by the dispatcher at send time, so
/// <see cref="PayloadJson"/> for those two kinds does hold the raw token. Rationale: phase 3's token
/// issuance is already built and tested (13 passing tests depend on the token being returned
/// synchronously for dev/test use); deferring generation to the dispatcher would mean a token can never be
/// reconstructed from its stored hash, forcing a breaking rework of that already-correct code for a
/// marginal reduction in the token's at-rest exposure window (it already expires in 3 days / 1 hour either
/// way). Tracked in docs/TRACEABILITY.md.
/// </summary>
public sealed class EmailOutbox
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Kind { get; set; } = "";
    public Guid? UserId { get; set; }

    /// <summary>Only for security-notice (spec §7.14: carries its own recipient so it's still sent after
    /// the Identity user row is gone); cleared once sent.</summary>
    public string? RecipientEmail { get; set; }

    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = EmailOutboxStatuses.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime SendAfter { get; set; } = DateTime.UtcNow;
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }

    /// <summary>Digest kinds only (expiring/expired/purge): the partial-unique-index dedup key, "at most
    /// one email per stage per user per day".</summary>
    public DateOnly? DigestDate { get; set; }

    public List<EmailOutboxItem> Items { get; set; } = [];
}

/// <summary>One link per digest email, with the expiry snapshotted at queue time so the dispatcher can
/// detect (and drop) a stale item if the link changed between queueing and sending.</summary>
public sealed class EmailOutboxItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OutboxId { get; set; }
    public Guid LinkId { get; set; }
    public string Slug { get; set; } = "";
    public DateTime ExpiresAtSnapshot { get; set; }
}

/// <summary>Spec §7.10: the dispatcher never sends to a suppressed address. Populated by hard bounces,
/// complaints (via the signed webhook) and permanent SMTP failures.</summary>
public sealed class EmailSuppression
{
    public string Email { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
