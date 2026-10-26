using Microsoft.Extensions.Configuration;
using Zaagl.Modules.Analytics.Infrastructure;

namespace Zaagl.Links.Tests;

public class RawEventBudgetTests
{
    private static RawEventBudget MakeBudget(int perHour)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Analytics:RawEventsPerLinkPerHour"] = perHour.ToString() })
            .Build();
        return new RawEventBudget(config);
    }

    [Fact]
    public void Allows_up_to_the_configured_cap_then_refuses()
    {
        var budget = MakeBudget(3);
        var link = Guid.NewGuid();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(budget.TryConsume(link, now));
        Assert.True(budget.TryConsume(link, now.AddMinutes(1)));
        Assert.True(budget.TryConsume(link, now.AddMinutes(2)));
        Assert.False(budget.TryConsume(link, now.AddMinutes(3))); // 4th raw row this hour: refused
        Assert.False(budget.TryConsume(link, now.AddMinutes(4)));
    }

    [Fact]
    public void Resets_for_a_new_hour()
    {
        var budget = MakeBudget(1);
        var link = Guid.NewGuid();
        var hour1 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var hour2 = hour1.AddHours(1);

        Assert.True(budget.TryConsume(link, hour1));
        Assert.False(budget.TryConsume(link, hour1.AddMinutes(30)));
        Assert.True(budget.TryConsume(link, hour2)); // new hour bucket, budget available again
    }

    [Fact]
    public void Different_links_have_independent_budgets()
    {
        var budget = MakeBudget(1);
        var now = DateTime.UtcNow;
        var linkA = Guid.NewGuid();
        var linkB = Guid.NewGuid();

        Assert.True(budget.TryConsume(linkA, now));
        Assert.False(budget.TryConsume(linkA, now));
        Assert.True(budget.TryConsume(linkB, now)); // independent of linkA's exhausted budget
    }
}
