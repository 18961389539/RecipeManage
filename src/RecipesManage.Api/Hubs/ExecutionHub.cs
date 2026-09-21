using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RecipesManage.Application.Contracts;

namespace RecipesManage.Api.Hubs;

[Authorize]
public sealed class ExecutionHub : Hub
{
    public Task SubscribeBatch(Guid batchId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, $"batch:{batchId:D}");

    public Task UnsubscribeBatch(Guid batchId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, $"batch:{batchId:D}");

    public Task SubscribeDashboard() =>
        Groups.AddToGroupAsync(Context.ConnectionId, "dashboard");
}

public sealed class SignalRExecutionPublisher : IExecutionPublisher
{
    private readonly IHubContext<ExecutionHub> _hub;
    public SignalRExecutionPublisher(IHubContext<ExecutionHub> hub) => _hub = hub;

    public async Task PublishAsync(ExecutionEvent evt, CancellationToken cancellationToken = default)
    {
        await _hub.Clients.Group($"batch:{evt.BatchId:D}").SendAsync("execution", evt, cancellationToken);
        await _hub.Clients.Group("dashboard").SendAsync("execution", evt, cancellationToken);
    }
}
