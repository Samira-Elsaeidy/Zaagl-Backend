namespace Zaagl.Cli.Commands;

// The JSON shapes the CLI prints with --json (stable output contract for scripts). Property names are camelCase.

public sealed record LinkResult(string Url, string Slug, Guid Id, string? Destination, DateTimeOffset? ExpiresAt);

public sealed record PasteResult(string Url, string Code, Guid ResourceId, long SizeBytes, int LineCount, DateTimeOffset ExpiresAt);

public sealed record LsRow(
    string Kind, string Code, string? Title, string Url, string? State, long? SizeBytes, long? Clicks,
    DateTimeOffset? ExpiresAt, DateTimeOffset? CreatedAt);

public sealed record WhoamiResult(
    string? Email, string? DisplayName, string? Role, string AuthMethod, string Host, Guid? SessionId,
    long? MaxSnippetBytes, int? RetentionDays);

public sealed record LoginResult(string? Email, string? DisplayName, string Host);

public sealed record LogoutResult(bool WasSignedIn, bool Revoked);

public sealed record ConfigEntry(string Key, string? Value, bool IsDefault);

public sealed record VersionResult(string Version, string Os, string Arch);
