using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Infrastructure;

/// <summary>
/// Live dashboard feed. Every connection is added to its owner's group (account-wide analytics) and can subscribe
/// to one of its own links. Each human click arrives as a "clicks" event carrying a list of clicks.
/// </summary>
[Authorize]
public sealed class AnalyticsHub(ILinkDirectory links) : Hub
{
    public static string LinkGroup(Guid linkId) => $"link:{linkId}";

    public static string OwnerGroup(Guid ownerId) => $"owner:{ownerId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, OwnerGroup(Context.User!.GetUserId()));
        await base.OnConnectedAsync();
    }

    public async Task Subscribe(Guid linkId)
    {
        var owned = await links.FindOwnedAsync(Context.User!.GetUserId(), linkId, Context.ConnectionAborted);
        if (owned is null) throw new HubException("Link not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, LinkGroup(linkId));
    }

    public Task Unsubscribe(Guid linkId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, LinkGroup(linkId));
}

public sealed class SignalRLiveClickPublisher(IHubContext<AnalyticsHub> hub) : ILiveClickPublisher
{
    public Task PublishAsync(IReadOnlyList<LiveClick> clicks, CancellationToken cancellationToken)
    {
        var sends = new List<Task>();
        foreach (var group in clicks.GroupBy(c => c.LinkId))
            sends.Add(hub.Clients.Group(AnalyticsHub.LinkGroup(group.Key)).SendAsync("clicks", group.ToList(), cancellationToken));
        foreach (var group in clicks.GroupBy(c => c.OwnerId))
            sends.Add(hub.Clients.Group(AnalyticsHub.OwnerGroup(group.Key)).SendAsync("clicks", group.ToList(), cancellationToken));
        return Task.WhenAll(sends);
    }
}
