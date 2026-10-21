using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Zaagl.Modules.Analytics.Contracts;
using Zaagl.Modules.Links.Contracts;

namespace Zaagl.Api.Infrastructure;

/// <summary>Live dashboard feed. A client subscribes to one of its own links and receives a "click" event per human click.</summary>
[Authorize]
public sealed class AnalyticsHub(ILinkDirectory links) : Hub
{
    public static string Group(Guid linkId) => $"link:{linkId}";

    public async Task Subscribe(Guid linkId)
    {
        var owned = await links.FindOwnedAsync(Context.User!.GetUserId(), linkId, Context.ConnectionAborted);
        if (owned is null) throw new HubException("Link not found.");
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(linkId));
    }

    public Task Unsubscribe(Guid linkId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(linkId));
}

public sealed class SignalRLiveClickPublisher(IHubContext<AnalyticsHub> hub) : ILiveClickPublisher
{
    public Task PublishAsync(IReadOnlyList<LiveClick> clicks, CancellationToken cancellationToken) =>
        Task.WhenAll(clicks.GroupBy(c => c.LinkId).Select(group =>
            hub.Clients.Group(AnalyticsHub.Group(group.Key)).SendAsync("clicks", group.ToList(), cancellationToken)));
}
