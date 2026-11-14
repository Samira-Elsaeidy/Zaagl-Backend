namespace Zaagl.Modules.Links.Domain;

/// <summary>
/// The guest-creation control panel's settings (one row, <c>id = 1</c>). No row means nothing was ever saved and the
/// configuration defaults (<c>Guests:*</c>) apply. Read through <c>GuestSettingsStore</c> (cached a few seconds), written only
/// by <c>GuestControlService</c>, which also writes a <see cref="GuestSettingsAuditRow"/> in the same transaction.
/// </summary>
public sealed class GuestSettingsRow
{
    public short Id { get; set; } = 1;

    public bool LinkEnabled { get; set; }
    public bool LinkCaptcha { get; set; }
    public bool UploadEnabled { get; set; }
    public bool UploadCaptcha { get; set; }
    public bool SnippetEnabled { get; set; }
    public bool SnippetCaptcha { get; set; }

    public int PerMinute { get; set; }
    public int GlobalPerHour { get; set; }
    public int ContentPerHour { get; set; }
    public int ContentPerDay { get; set; }
    public int LinksPerDay { get; set; }

    /// <summary>Bumped on every save (optimistic concurrency for the panel).</summary>
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public string? UpdatedByEmail { get; set; }
}

public static class GuestAuditActions
{
    public const string Update = "update";
    public const string PauseAll = "pause-all";
    public const string ResumeAll = "resume-all";
}

/// <summary>Who changed which guest setting, when, from what to what. Append-only; never shown to anyone but admins.</summary>
public sealed class GuestSettingsAuditRow
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorEmail { get; set; }
    public string Action { get; set; } = GuestAuditActions.Update;
    /// <summary>jsonb array of <c>{field, old, new}</c>.</summary>
    public string Changes { get; set; } = "[]";
    public string? Reason { get; set; }
}
