namespace Zaagl.Modules.Identity.Contracts;

public sealed record UserDto(Guid Id, string Email, string DisplayName);

public sealed record AuthResponse(string Token, UserDto User);

public sealed record RegisterRequest(string Email, string Password, string? DisplayName);

public sealed record LoginRequest(string Email, string Password);

public sealed record CreateApiKeyRequest(string? Name);

public sealed record ApiKeyDto(Guid Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

public sealed record CreatedApiKeyDto(ApiKeyDto Key, string Secret);
