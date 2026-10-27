using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zaagl.Modules.Identity.Application;

public sealed record EmailMessage(string ToEmail, string Subject, string TextBody, string HtmlBody);

/// <summary>Thrown for a permanent SMTP failure (e.g. mailbox does not exist): the dispatcher suppresses
/// the address and does not retry. Any other exception is treated as transient (retried with backoff).</summary>
public sealed class EmailPermanentFailureException(string message) : Exception(message);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed class EmailOptions
{
    public const string Section = "Email";
    public string From { get; set; } = "no-reply@zaa.gl";
    public string FromName { get; set; } = "Zaagl";
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>Spec §7.10: the outbox's daily digest run, UTC. Default 06:00.</summary>
    public TimeOnly DailyRunUtc { get; set; } = new(6, 0);

    /// <summary>Verifies <c>POST /api/webhooks/email</c> signatures (HMAC-SHA256 over the raw body).</summary>
    public string WebhookSecret { get; set; } = "";

    /// <summary>Where /account/confirm-email and /account/reset-password are actually served (spec §1.2:
    /// Hosts:Api) - used to build the fragment URL put in confirm/reset emails.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.zaa.gl";
}

/// <summary>Real SMTP delivery via the BCL's SmtpClient - no external mail library dependency needed for
/// this build's scope. 5xx responses are treated as permanent (suppress); anything else as transient.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        var opts = options.Value;
        using var client = new SmtpClient(opts.SmtpHost, opts.SmtpPort) { EnableSsl = opts.SmtpUseSsl };
        if (!string.IsNullOrEmpty(opts.SmtpUser))
            client.Credentials = new NetworkCredential(opts.SmtpUser, opts.SmtpPassword);

        using var mail = new MailMessage
        {
            From = new MailAddress(opts.From, opts.FromName),
            Subject = message.Subject,
            Body = message.HtmlBody,
            IsBodyHtml = true,
        };
        mail.To.Add(message.ToEmail);
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.TextBody, null, "text/plain"));

        try
        {
            await client.SendMailAsync(mail, ct);
        }
        catch (SmtpFailedRecipientException ex) when ((int)ex.StatusCode >= 500)
        {
            throw new EmailPermanentFailureException($"Permanent SMTP failure: {ex.StatusCode}");
        }
    }
}

/// <summary>Dev-mode fallback used when no SMTP host is configured (same shape as WebRiskChecker's
/// "no API key -> skip" and CodeGenerator's dev-only scramble key fallback): logs the full rendered email
/// instead of sending it, so confirm-email/reset links are visible in the console during local dev.</summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation(
            "DEV EMAIL (no SMTP configured) -> {To}\nSubject: {Subject}\n{Body}",
            message.ToEmail, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }
}

/// <summary>Plain text + simple HTML templates (spec §7.10: "never target URLs" for link warnings;
/// confirm/reset links carry their token in the URL fragment).</summary>
public static class EmailTemplates
{
    public static EmailMessage ConfirmEmail(string toEmail, string fragmentUrl) => Build(
        toEmail, "Confirm your Zaagl account",
        $"Confirm your email address to finish setting up your Zaagl account:\n\n{fragmentUrl}\n\nThis link expires in 3 days.",
        $"<p>Confirm your email address to finish setting up your Zaagl account:</p><p><a href=\"{fragmentUrl}\">Confirm my email</a></p><p>This link expires in 3 days.</p>");

    public static EmailMessage PasswordReset(string toEmail, string fragmentUrl) => Build(
        toEmail, "Reset your Zaagl password",
        $"Reset your password:\n\n{fragmentUrl}\n\nThis link expires in 1 hour. If you did not request this, ignore this email.",
        $"<p>Reset your password:</p><p><a href=\"{fragmentUrl}\">Reset my password</a></p><p>This link expires in 1 hour. If you did not request this, ignore this email.</p>");

    public static EmailMessage AccountExists(string toEmail) => Build(
        toEmail, "Someone tried to register this address",
        "Someone tried to create a Zaagl account with this email address, which already has one. If this was you, sign in instead; if not, no action is needed.",
        "<p>Someone tried to create a Zaagl account with this email address, which already has one. If this was you, sign in instead; if not, no action is needed.</p>");

    public static EmailMessage SecurityNotice(string toEmail, string notice) => Build(
        toEmail, "Security notice for your Zaagl account", notice, $"<p>{notice}</p>");

    public static EmailMessage Digest(string toEmail, string stage, IReadOnlyList<(string Slug, DateTime Date)> links)
    {
        var (subject, lead) = stage switch
        {
            Domain.EmailKinds.Expiring => ("Links expiring soon", "These links stop working soon. Renew them if you still need them:"),
            Domain.EmailKinds.Expired => ("Links have expired", "These links now return 410 and will be deleted if not renewed:"),
            _ => ("Links will be deleted soon", "These links are scheduled for deletion. This is the final warning:"),
        };
        var textLines = links.Select(l => $"- {l.Slug} ({l.Date:yyyy-MM-dd})");
        var htmlItems = links.Select(l => $"<li>{l.Slug} ({l.Date:yyyy-MM-dd})</li>");
        return Build(toEmail, subject,
            $"{lead}\n\n{string.Join("\n", textLines)}\n\nManage your links at https://app.zaa.gl/links",
            $"<p>{lead}</p><ul>{string.Join("", htmlItems)}</ul><p><a href=\"https://app.zaa.gl/links\">Manage your links</a></p>");
    }

    private static EmailMessage Build(string toEmail, string subject, string text, string html) => new(toEmail, subject, text, html);
}
