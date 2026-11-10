using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zaagl.Modules.Identity.Contracts;
using Zaagl.Modules.Identity.Domain;

namespace Zaagl.Modules.Identity.Application;

public sealed class JwtKeyEntry
{
    public string Kid { get; set; } = "";
    public string Key { get; set; } = "";
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Issuer { get; set; } = "zaagl";
    public string Audience { get; set; } = "zaagl";

    /// <summary>Back-compat single key, used when <see cref="Keys"/> is empty (this app's existing dev
    /// config shape). Wrapped as one entry with kid "default".</summary>
    public string Key { get; set; } = "";

    /// <summary>Spec §3/§7.4: a JSON set of {kid, key} entries so multiple keys can validate while only
    /// the newest signs - the mechanism a real key rotation needs, even with just one entry configured.</summary>
    public List<JwtKeyEntry> Keys { get; set; } = [];

    /// <summary>Spec §7.4: 15 minutes.</summary>
    public int ExpiryMinutes { get; set; } = 15;

    public IReadOnlyList<JwtKeyEntry> EffectiveKeys() =>
        Keys.Count > 0 ? Keys : [new JwtKeyEntry { Kid = "default", Key = Key }];

    public JwtKeyEntry SigningKeyEntry() => EffectiveKeys()[^1]; // the newest (last configured) signs

    public IReadOnlyList<SecurityKey> AllValidationKeys() =>
        EffectiveKeys().Select(k => (SecurityKey)new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k.Key)) { KeyId = k.Kid }).ToList();
}

public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider? time = null)
{
    public const string EnrolOnlyPurposeClaim = "enrol_only";

    private readonly JwtOptions _options = options.Value;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private static readonly string[] AllScopes =
        [TokenScopes.LinksRead, TokenScopes.LinksWrite, TokenScopes.StatsRead,
         TokenScopes.SnippetsWrite, TokenScopes.FilesWrite, TokenScopes.ResourcesRead];

    public (string Token, DateTime ExpiresAt) IssueAccessToken(User user) => IssueAccessToken(user, null);

    /// <summary>ARC-014 §2.4: a device (CLI/desktop) access token carries <c>client_id</c>, <c>amr=device</c> and
    /// <c>sid</c> (the refresh family), and never the role claim, even for an Admin account.</summary>
    public (string Token, DateTime ExpiresAt) IssueDeviceAccessToken(User user, string clientId, Guid sessionId) =>
        IssueAccessToken(user, (clientId, sessionId));

    private (string Token, DateTime ExpiresAt) IssueAccessToken(User user, (string ClientId, Guid SessionId)? device)
    {
        var expires = _time.GetUtcNow().UtcDateTime.AddMinutes(_options.ExpiryMinutes);
        var signingEntry = _options.SigningKeyEntry();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new("sstamp", user.SecurityStamp.ToString()),
        };
        // An interactive or device session has every non-admin scope; an API key is the only principal with a narrower set.
        claims.AddRange(AllScopes.Select(s => new Claim("scope", s)));
        if (device is { } d)
        {
            claims.Add(new Claim(NativeClientClaims.ClientId, d.ClientId));
            claims.Add(new Claim(NativeClientClaims.Amr, NativeClientClaims.AmrDevice));
            claims.Add(new Claim(NativeClientClaims.SessionId, d.SessionId.ToString()));
        }
        else if (user.Role is { } role) claims.Add(new Claim(ClaimTypes.Role, role));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingEntry.Key)) { KeyId = signingEntry.Kid },
                SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(claims),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>Spec §7.4: issued after a correct password for an Admin without an enrolled authenticator.
    /// Valid only for /totp/enroll and /totp/confirm - callers must check the <see cref="EnrolOnlyPurposeClaim"/>
    /// claim and reject it everywhere else.</summary>
    public (string Token, DateTime ExpiresAt) IssueEnrolOnlyToken(User user)
    {
        var expires = DateTime.UtcNow.AddMinutes(10);
        var signingEntry = _options.SigningKeyEntry();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingEntry.Key)) { KeyId = signingEntry.Kid },
                SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity([
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("sstamp", user.SecurityStamp.ToString()),
                new Claim(EnrolOnlyPurposeClaim, "true"),
            ]),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
