using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Domain;
using Zaagl.Modules.Identity.Infrastructure;
using Zaagl.SharedKernel;

namespace Zaagl.Modules.Identity.Application;

public sealed class IdentityService(IdentityDbContext db, TokenService tokens)
{
    private const string ApiKeyPrefix = "zgl_";
    private const int MaxApiKeysPerUser = 20;

    public async Task<Result<AuthResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null) return Error.Validation("A valid email address is required.");
        if (request.Password is not { Length: >= 8 and <= 128 })
            return Error.Validation("Password must be between 8 and 128 characters.");

        var user = new User
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            DisplayName = Truncate(request.DisplayName, 200) ?? email[..email.IndexOf('@')],
        };
        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("An account with this email already exists.");
        }
        return Respond(user);
    }

    public async Task<Result<AuthResponse>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = email is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // One error for unknown user, social-only account and wrong password.
        if (user?.PasswordHash is null || !BCrypt.Net.BCrypt.Verify(request.Password ?? "", user.PasswordHash))
            return Error.Unauthorized("Invalid email or password.");
        return Respond(user);
    }

    public Task<UserDto?> GetUserAsync(Guid userId, CancellationToken ct) =>
        db.Users.Where(u => u.Id == userId)
            .Select(u => new UserDto(u.Id, u.Email, u.DisplayName))
            .FirstOrDefaultAsync(ct);

    public async Task<Result<AuthResponse>> ExternalLoginAsync(
        string provider, string providerUserId, string? email, string? name, CancellationToken ct)
    {
        provider = provider.ToLowerInvariant();
        var existing = await db.ExternalLogins
            .FirstOrDefaultAsync(l => l.Provider == provider && l.ProviderUserId == providerUserId, ct);
        if (existing is not null)
            return Respond(await db.Users.FirstAsync(u => u.Id == existing.UserId, ct));

        var normalized = NormalizeEmail(email);
        if (normalized is null) return Error.Validation("The provider did not return a usable email address.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);
        if (user is null)
        {
            user = new User
            {
                Email = normalized,
                DisplayName = Truncate(name, 200) ?? normalized[..normalized.IndexOf('@')],
            };
            db.Users.Add(user);
        }
        db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = provider, ProviderUserId = providerUserId });
        await db.SaveChangesAsync(ct);
        return Respond(user);
    }

    public async Task<IReadOnlyList<ApiKeyDto>> ListApiKeysAsync(Guid userId, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.UserId == userId && k.RevokedAt == null)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyDto(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt))
            .ToListAsync(ct);

    public async Task<Result<CreatedApiKeyDto>> CreateApiKeyAsync(Guid userId, string? name, CancellationToken ct)
    {
        if (await db.ApiKeys.CountAsync(k => k.UserId == userId && k.RevokedAt == null, ct) >= MaxApiKeysPerUser)
            return Error.Conflict($"You can have at most {MaxApiKeysPerUser} active API keys.");

        var secret = ApiKeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var key = new ApiKey
        {
            UserId = userId,
            Name = Truncate(name, 100) ?? "API key",
            Prefix = secret[..8],
            KeyHash = Hash(secret),
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return Result<CreatedApiKeyDto>.Ok(new(new(key.Id, key.Name, key.Prefix, key.CreatedAt, null), secret));
    }

    public async Task<bool> RevokeApiKeyAsync(Guid userId, Guid keyId, CancellationToken ct) =>
        await db.ApiKeys.Where(k => k.Id == keyId && k.UserId == userId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, DateTime.UtcNow), ct) > 0;

    /// <summary>Returns the owning user id for a raw API key, or null when invalid or revoked.</summary>
    public async Task<Guid?> ValidateApiKeyAsync(string rawKey, CancellationToken ct)
    {
        if (!rawKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal)) return null;
        var hash = Hash(rawKey);
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key is null) return null;

        if (key.LastUsedAt is null || key.LastUsedAt < DateTime.UtcNow.AddMinutes(-5))
            await db.ApiKeys.Where(k => k.Id == key.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow), ct);
        return key.UserId;
    }

    private AuthResponse Respond(User user) =>
        new(tokens.Issue(user), new UserDto(user.Id, user.Email, user.DisplayName));

    private static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320) return null;
        var trimmed = email.Trim();
        return MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed
            ? trimmed.ToLowerInvariant()
            : null;
    }

    private static string? Truncate(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
