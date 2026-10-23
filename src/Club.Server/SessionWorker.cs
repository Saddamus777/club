namespace Club.Server;

public sealed class SessionWorker(ClubStore store, ILogger<SessionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextBackup = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // A failure to persist time stops this service/host instead of silently allowing free sessions.
            store.Tick();
            if (DateTimeOffset.UtcNow < nextBackup) continue;
            try
            {
                store.Backup(null);
                nextBackup = DateTimeOffset.UtcNow.AddHours(6);
                logger.LogInformation("Automatic local backup verified.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Local backup failed; retry in five minutes.");
                nextBackup = DateTimeOffset.UtcNow.AddMinutes(5);
            }
        }
    }
}
