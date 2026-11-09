using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zaagl.Cli.Auth;
using Zaagl.Cli.Commands;
using Zaagl.Cli.Http;
using Zaagl.Cli.Infrastructure;

namespace Zaagl.Cli;

/// <summary>The single source-generated JSON context (reflection-based serialization is disabled in this project).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProblemDto))]
[JsonSerializable(typeof(OAuthErrorDto))]
[JsonSerializable(typeof(DeviceCodeRequestDto))]
[JsonSerializable(typeof(DeviceCodeResponseDto))]
[JsonSerializable(typeof(DeviceTokenRequestDto))]
[JsonSerializable(typeof(AuthResponseDto))]
[JsonSerializable(typeof(RefreshRequestDto))]
[JsonSerializable(typeof(LogoutRequestDto))]
[JsonSerializable(typeof(LinkCreateRequestDto))]
[JsonSerializable(typeof(LinkDto))]
[JsonSerializable(typeof(LinkPageDto))]
[JsonSerializable(typeof(SnippetCreatedDto))]
[JsonSerializable(typeof(ResourcePageDto))]
[JsonSerializable(typeof(StorageDto))]
[JsonSerializable(typeof(WhoamiDto))]
[JsonSerializable(typeof(StoredCredential))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ErrorEnvelope))]
[JsonSerializable(typeof(LinkResult))]
[JsonSerializable(typeof(PasteResult))]
[JsonSerializable(typeof(LsRow))]
[JsonSerializable(typeof(WhoamiResult))]
[JsonSerializable(typeof(LoginResult))]
[JsonSerializable(typeof(LogoutResult))]
[JsonSerializable(typeof(ConfigEntry))]
[JsonSerializable(typeof(VersionResult))]
internal partial class CliJsonContext : JsonSerializerContext;

internal static class CliJson
{
    /// <summary>Compact output; non-ASCII and URL characters (+, &amp;) printed as-is so output stays readable. The
    /// output goes to a terminal or a pipe, never into HTML, so relaxed escaping is safe here.</summary>
    public static readonly CliJsonContext Context = new(new JsonSerializerOptions(CliJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public static readonly CliJsonContext Indented = new(new JsonSerializerOptions(CliJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    });
}
