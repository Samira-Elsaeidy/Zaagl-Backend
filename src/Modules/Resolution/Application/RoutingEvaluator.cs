using Zaagl.Modules.Links.Contracts;
using Zaagl.Modules.Links.Domain;

namespace Zaagl.Modules.Resolution.Application;

/// <summary>
/// Picks the destination for a visitor. Device and country rules are checked in priority order and the
/// first match wins; then A/B split variants are picked by weight; otherwise the link's own destination is used.
/// </summary>
public static class RoutingEvaluator
{
    public static string Choose(LinkSnapshot link, string platform, string? country, Func<int, int>? roll = null)
    {
        foreach (var rule in link.Rules)
        {
            var matches = rule.Kind switch
            {
                RuleKinds.Device => string.Equals(rule.Match, platform, StringComparison.OrdinalIgnoreCase),
                RuleKinds.Country => country is not null && string.Equals(rule.Match, country, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
            if (matches) return rule.Url;
        }

        var variants = link.Rules.Where(r => r.Kind == RuleKinds.Split).ToList();
        if (variants.Count > 0)
        {
            var total = variants.Sum(v => Math.Max(1, v.Weight));
            var pick = (roll ?? Random.Shared.Next)(total);
            foreach (var variant in variants)
            {
                pick -= Math.Max(1, variant.Weight);
                if (pick < 0) return variant.Url;
            }
            return variants[^1].Url;
        }

        return link.DestinationUrl;
    }
}
