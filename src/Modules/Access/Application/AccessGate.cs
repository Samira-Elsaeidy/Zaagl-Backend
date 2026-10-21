using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Modules.Access.Application;

public enum AccessOutcome
{
    Allowed,
    Locked,
    Expired,
}

/// <summary>
/// Decides whether a visitor may pass a link's gates. Click limits are not decided here:
/// they are enforced atomically in PostgreSQL by <see cref="ILinkResolver.TryConsumeClickAsync"/>.
/// </summary>
public sealed class AccessGate
{
    public AccessOutcome Evaluate(LinkSnapshot link, DateTimeOffset now)
    {
        if (!link.IsActive) return AccessOutcome.Expired;
        if (link.ExpiresAt is { } expires && new DateTimeOffset(DateTime.SpecifyKind(expires, DateTimeKind.Utc)) <= now)
            return AccessOutcome.Expired;
        return link.HasPassword ? AccessOutcome.Locked : AccessOutcome.Allowed;
    }

    public bool VerifyPassword(LinkSnapshot link, string? password) =>
        link.HasPassword
        && !string.IsNullOrEmpty(password)
        && BCrypt.Net.BCrypt.Verify(password, link.PasswordHash);
}
