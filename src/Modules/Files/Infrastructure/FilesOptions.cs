namespace Zaagl.Modules.Files.Infrastructure;

/// <summary><c>Uploads:*</c> (ARC-009 §1).</summary>
public sealed class UploadOptions
{
    public const string Section = "Uploads";

    /// <summary>Largest PATCH body (PD-12, Cloudflare): 90,000,000 bytes.</summary>
    public long MaxChunkBytes { get; set; } = 90_000_000;

    public int SessionIdleHours { get; set; } = 24;

    /// <summary>Kinds the uploader accepts, comma separated: <c>media</c> in slice 1, <c>media,file</c> from slice 2.</summary>
    public string Kinds { get; set; } = "media";

    /// <summary>False until slice 2: a request with no credential answers <c>503 guest_creation_paused</c>.</summary>
    public bool GuestsEnabled { get; set; }

    /// <summary>Finalized session rows are deleted after this many days (ARC-009 §12).</summary>
    public int FinalizedRetentionDays { get; set; } = 7;

    /// <summary>PD-16 interim (ARC-009 9.2): who may not upload executables, comma separated: <c>guest</c>, <c>free</c>.</summary>
    public string BlockExecutablesFor { get; set; } = "guest,free";

    public bool BlocksExecutablesFor(bool isGuest, bool isPaid)
    {
        var tokens = BlockExecutablesFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return isGuest ? tokens.Contains("guest", StringComparer.OrdinalIgnoreCase) : !isPaid && tokens.Contains("free", StringComparer.OrdinalIgnoreCase);
    }

    public bool KindEnabled(string kind) =>
        Kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(kind, StringComparer.OrdinalIgnoreCase);
}

/// <summary><c>Scan:*</c> (ARC-009 §1, §10).</summary>
public sealed class ScanOptions
{
    public const string Section = "Scan";

    /// <summary><c>Clamd</c> or <c>Fake</c>. <c>Fake</c> only in Development; any other environment fails at startup.</summary>
    public string Provider { get; set; } = "Clamd";

    public int Concurrency { get; set; } = 2;

    /// <summary>Delay before the next attempt (minutes); the last value repeats.</summary>
    public int[] RetryMinutes { get; set; } = [1, 5, 15, 60];

    public int MaxAttempts { get; set; } = 6;

    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary><c>ClamAv:*</c>, the key names docker-compose.yml already sets (<c>ClamAv__Host</c> and so on).</summary>
public sealed class ClamAvOptions
{
    public const string Section = "ClamAv";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;

    /// <summary>Always honoured as true: clamd down, timing out or answering anything but OK leaves the file unscanned and
    /// never served (addendum §7.1). A configured <c>false</c> is logged and ignored.</summary>
    public bool FailClosed { get; set; } = true;

    /// <summary><c>zSCAN &lt;path&gt;</c> on a shared read-only mount instead of streaming (INSTREAM, the default).</summary>
    public bool ScanByPath { get; set; }

    /// <summary>INSTREAM chunk size.</summary>
    public int StreamChunkBytes { get; set; } = 64 * 1024;
}
