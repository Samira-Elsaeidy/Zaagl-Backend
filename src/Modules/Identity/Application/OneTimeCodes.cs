using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;

namespace Zaagl.Modules.Identity.Application;

/// <summary>
/// PD-41: short-lived, single-use codes bound to one user (social sign-in exchange code, SignalR hub ticket).
/// The raw value is 32 random bytes (base64url) handed to the caller once; only its SHA-256 hash is stored,
/// in PostgreSQL so every API instance sees it. Consumption is a conditional DELETE, so under any concurrency
/// exactly one caller wins.
/// </summary>
public sealed class OneTimeCodeService(IdentityDbContext db, TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Expired rows older than this are swept opportunistically on every issue.</summary>
    private static readonly TimeSpan SweepAfter = TimeSpan.FromMinutes(10);

    public sealed record Issued(string Code, DateTime ExpiresAt);

    public async Task<Issued> IssueAsync(Guid userId, string purpose, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var raw = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = now.Add(Lifetime);

        await db.OneTimeCodes.Where(c => c.ExpiresAt < now - SweepAfter).ExecuteDeleteAsync(ct);
        db.OneTimeCodes.Add(new OneTimeCode
        {
            CodeHash = Hash(raw),
            Purpose = purpose,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = expires,
        });
        await db.SaveChangesAsync(ct);
        return new Issued(raw, expires);
    }

    /// <summary>Consumes the code: the user it was issued to, or null when it is unknown, expired, already used
    /// or issued for another purpose. Only one of several concurrent callers gets the user.</summary>
    public async Task<Guid?> ConsumeAsync(string? code, string purpose, CancellationToken ct)
    {
        if (!LooksValid(code)) return null;
        var hash = Hash(code!);
        var now = time.GetUtcNow().UtcDateTime;

        var row = await db.OneTimeCodes.AsNoTracking()
            .Where(c => c.CodeHash == hash && c.Purpose == purpose && c.ExpiresAt > now)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var deleted = await db.OneTimeCodes
            .Where(c => c.CodeHash == hash && c.Purpose == purpose && c.ExpiresAt > now)
            .ExecuteDeleteAsync(ct);
        return deleted == 1 ? row : null;
    }

    /// <summary>Reads the code without consuming it (SignalR negotiate, which precedes the real connect).</summary>
    public async Task<Guid?> PeekAsync(string? code, string purpose, CancellationToken ct)
    {
        if (!LooksValid(code)) return null;
        var hash = Hash(code!);
        var now = time.GetUtcNow().UtcDateTime;
        return await db.OneTimeCodes.AsNoTracking()
            .Where(c => c.CodeHash == hash && c.Purpose == purpose && c.ExpiresAt > now)
            .Select(c => (Guid?)c.UserId)
            .FirstOrDefaultAsync(ct);
    }

    // 32 bytes base64url = 43 characters; anything else is rejected before hashing or touching the database.
    private static bool LooksValid(string? code) =>
        code is { Length: 43 } && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
