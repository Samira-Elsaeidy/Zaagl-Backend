using Zaagl.Api.Infrastructure;
using Zaagl.Modules.Links.Application;

namespace Zaagl.Api.Endpoints;

/// <summary>
/// Spec §7.12 CAPTCHA check at the endpoint layer, for routes outside the Links module (the verifier lives there, so
/// only Zaagl.Api references it and the Identity module stays free of it). Reads the <c>Captcha-Token</c> header and
/// verifies it for <paramref name="action"/> against the frontend's hostname (<c>Frontend:BaseUrl</c>), the same way
/// the guest link, snippet and upload routes do. Missing token: 400 <c>captcha-required</c>; rejected token: 400
/// <c>captcha-invalid</c>; verifier down: 503 <c>service_unavailable</c> (never a silent pass). With no
/// <c>Captcha:Secret</c> the no-op verifier passes everything.
/// </summary>
public static class CaptchaGate
{
    public const string HeaderName = "Captcha-Token";

    /// <summary>Spec §7.12 action names, shared with the website (src/components/captcha.tsx CaptchaAction).</summary>
    public const string Register = "register";
    public const string ForgotPassword = "forgot-password";
    public const string ResendConfirmation = "resend-confirmation";

    /// <summary>Null when the token verified; otherwise the problem to return as-is.</summary>
    public static async Task<IResult?> CheckAsync(HttpContext http, string action, CancellationToken ct)
    {
        var services = http.RequestServices;
        var config = services.GetRequiredService<IConfiguration>();
        var token = http.Request.Headers[HeaderName].ToString();
        var frontendHost = Uri.TryCreate(config["Frontend:BaseUrl"], UriKind.Absolute, out var frontendUri) ? frontendUri.Host : "";
        var verified = await services.GetRequiredService<ICaptchaVerifier>()
            .VerifyAsync(string.IsNullOrEmpty(token) ? null : token, action, frontendHost, ct);
        return verified.IsSuccess ? null : ApiResults.Problem(verified.Error!);
    }
}
