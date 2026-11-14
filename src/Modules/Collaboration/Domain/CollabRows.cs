namespace Zaagl.Modules.Collaboration.Domain;

/// <summary><c>collab.comparisons</c> (docs/phase2/compare-slider.md): a before/after pair of the owner's image resources. No
/// cross-schema foreign keys; the parts are re-checked on every read.</summary>
public sealed class ComparisonRow
{
    public Guid ResourceId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid BeforeResourceId { get; set; }
    public Guid AfterResourceId { get; set; }
    public string? BeforeLabel { get; set; }
    public string? AfterLabel { get; set; }
    public DateTime CreatedAt { get; set; }
}
