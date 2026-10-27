namespace Zaagl.Modules.Identity.Domain;

public sealed class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Email { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Spec §7.4: an unconfirmed account cannot sign in. Social-login users are auto-confirmed
    /// (the provider already verified the address).</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>Bumped on password change/reset and on logout-everywhere; a JWT's "sstamp" claim must
    /// match this for the token to still be honoured (spec §7.4).</summary>
    public Guid SecurityStamp { get; set; } = Guid.CreateVersion7();

    /// <summary>Null for ordinary users. "Admin" requires TOTP (spec §7.4).</summary>
    public string? Role { get; set; }

    /// <summary>Base32 TOTP secret, set once an admin enrols. Null until enrolled.</summary>
    public string? TotpSecret { get; set; }
    public DateTime? TotpConfirmedAt { get; set; }

    public DateTime? DisabledAt { get; set; }
    public string? DisabledReason { get; set; }

    /// <summary>Spec §7.4: whether an admin disable also disabled all of this user's links. Read back on
    /// enable (`restoreLinks?`) to tell "bring the links back" apart from "leave them disabled".</summary>
    public bool DisabledWithLinks { get; set; }
}

public sealed class ExternalLogin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderUserId { get; set; } = "";
}

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    /// <summary>Spec §4.4: links:read, links:write, stats:read.</summary>
    public string[] Scopes { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public enum EmailTokenKind
{
    ConfirmEmail,
    PasswordReset,
}

/// <summary>Spec §7.4: confirmation/reset tokens travel in a URL fragment and are generated fresh each
/// time; only their hash is stored, never the raw value (defense in depth even in dev, where nothing
/// actually emails them yet - phase 5 wires real delivery).</summary>
public sealed class EmailToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public EmailTokenKind Kind { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

/// <summary>Spec §4.4 + §7.4: rotated on every use, grouped into a family so a replayed (already-used)
/// token revokes the whole family - the actual theft-detection mechanism.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Spec §7.4 login protection: a failed password is recorded per (account, IP partition) so a
/// pair can be locked for 15 minutes after 5 failures, and per-account across all partitions so a
/// progressive delay (not a hard lockout) applies regardless of where the attempts came from.</summary>
public sealed class LoginFailure
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public string IpPartition { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
