using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WizarrRequests;

public sealed class UserDeletedConsumer(SeerrCleanupStore store, ILogger<UserDeletedConsumer> logger) : IEventConsumer<UserDeletedEventArgs>
{
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        if (Plugin.Instance.Configuration.SeerrCleanupEnabled)
        {
            try { store.Enqueue(eventArgs.Argument.Id); }
            catch (Exception) { logger.LogError("Could not persist Seerr cleanup for deleted user {UserId}; manual cleanup required.", eventArgs.Argument.Id); }
        }
        return Task.CompletedTask;
    }
}

public sealed class SeerrCleanupWorker(SeerrCleanupProcessor processor, IUserManager users, ILogger<SeerrCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await processor.RunOnce(Plugin.Instance.Configuration, id => users.GetUserById(id) is not null, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogError("Seerr cleanup worker could not process its queue; Jellyfin remains running."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
