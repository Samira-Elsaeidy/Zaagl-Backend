namespace Zaagl.Modules.Identity.Domain;

public enum DeviceAuthorizationStatus : short
{
    Pending = 0,
    Approved = 1,
    Denied = 2,
    Redeemed = 3,
}

/// <summary>ARC-014 §2.1: one RFC 8628 device authorization. The device code and the user code are stored only as
/// SHA-256 hashes (the raw values are shown once).</summary>
public sealed class DeviceAuthorization
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public byte[] DeviceCodeHash { get; set; } = [];
    public byte[] UserCodeHash { get; set; } = [];
    public string ClientId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? Os { get; set; }
    public DeviceAuthorizationStatus Status { get; set; }
    public Guid? UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public int PollIntervalSeconds { get; set; }
}

/// <summary>ARC-014 §2.4: the device session is the refresh-token family (<see cref="FamilyId"/> = the family of
/// <see cref="RefreshToken"/>, and the <c>sid</c> claim). Its presence is what makes a refresh issue a device token.</summary>
public sealed class DeviceSession
{
    public Guid FamilyId { get; set; }
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = "";
    public string? DeviceName { get; set; }
    public string? Os { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>Brute-force guard on user codes: one row per wrong code a user typed. The row that reaches the limit
/// carries <see cref="LockedUntil"/>.</summary>
public sealed class DeviceActivationFailure
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LockedUntil { get; set; }
}
