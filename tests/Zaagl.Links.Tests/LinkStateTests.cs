using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Links.Tests;

/// <summary>Exhaustive coverage of spec §4.3's state function, exactly as mandated by the spec itself.</summary>
public class LinkStateTests
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past = Now.AddDays(-1);
    private static readonly DateTime Future = Now.AddDays(1);

    [Fact]
    public void Active_when_nothing_is_set()
    {
        Assert.Equal(LinkState.Active, LinkStateCalculator.Compute(null, null, null, null, Now));
    }

    [Fact]
    public void Active_when_expiry_is_in_the_future()
    {
        Assert.Equal(LinkState.Active, LinkStateCalculator.Compute(null, null, null, Future, Now));
    }

    [Fact]
    public void Quarantined_when_expiry_has_passed_and_nothing_else_is_set()
    {
        Assert.Equal(LinkState.Quarantined, LinkStateCalculator.Compute(null, null, null, Past, Now));
    }

    [Fact]
    public void Quarantined_at_the_exact_expiry_instant()
    {
        Assert.Equal(LinkState.Quarantined, LinkStateCalculator.Compute(null, null, null, Now, Now));
    }

    [Fact]
    public void Disabled_takes_priority_over_quarantined()
    {
        Assert.Equal(LinkState.Disabled, LinkStateCalculator.Compute(null, null, Past, Past, Now));
    }

    [Fact]
    public void Disabled_even_without_an_expiry()
    {
        Assert.Equal(LinkState.Disabled, LinkStateCalculator.Compute(null, null, Past, null, Now));
    }

    [Fact]
    public void Deleted_takes_priority_over_disabled_and_quarantined()
    {
        Assert.Equal(LinkState.Deleted, LinkStateCalculator.Compute(null, Past, Past, Past, Now));
    }

    [Fact]
    public void Purgeable_takes_priority_over_everything()
    {
        Assert.Equal(LinkState.Purgeable, LinkStateCalculator.Compute(Past, Past, Past, Past, Now));
        Assert.Equal(LinkState.Purgeable, LinkStateCalculator.Compute(Past, null, null, null, Now));
    }

    [Fact]
    public void Purgeable_at_the_exact_purge_after_instant()
    {
        Assert.Equal(LinkState.Purgeable, LinkStateCalculator.Compute(Now, null, null, null, Now));
    }

    [Fact]
    public void Not_yet_purgeable_the_instant_before()
    {
        Assert.Equal(LinkState.Active, LinkStateCalculator.Compute(Now.AddTicks(1), null, null, null, Now));
    }

    /// <summary>Spec §4.3's own invariant, tested directly: once expires_at has passed, the function can
    /// never return Active - regardless of whether any purge job has ever run (there is none in this
    /// build; purgeable rows simply stay rows).</summary>
    [Theory]
    [MemberData(nameof(NotYetPurgeableOffsets))]
    public void Never_active_once_expiry_has_passed(DateTime? purgeAfter)
    {
        var state = LinkStateCalculator.Compute(purgeAfter, null, null, Past, Now);
        Assert.NotEqual(LinkState.Active, state);
    }

    public static IEnumerable<object?[]> NotYetPurgeableOffsets()
    {
        yield return [null]; // no purge_after set yet
        yield return [Now.AddMonths(2)]; // purge_after far in the future
        yield return [Now.AddDays(1)]; // purge_after one day after Now, still not purgeable
    }

    [Fact]
    public void All_five_states_are_reachable()
    {
        var seen = new HashSet<LinkState>
        {
            LinkStateCalculator.Compute(null, null, null, null, Now), // Active
            LinkStateCalculator.Compute(null, null, null, Past, Now), // Quarantined
            LinkStateCalculator.Compute(null, null, Past, null, Now), // Disabled
            LinkStateCalculator.Compute(null, Past, null, null, Now), // Deleted
            LinkStateCalculator.Compute(Past, null, null, null, Now), // Purgeable
        };
        Assert.Equal(5, seen.Count);
    }
}
