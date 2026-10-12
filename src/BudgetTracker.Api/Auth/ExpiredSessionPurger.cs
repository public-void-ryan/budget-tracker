using Microsoft.Extensions.Options;

namespace BudgetTracker.Api.Auth;

/// <summary>
/// Reclaims expired session rows on a fixed interval. A failed purge is logged and retried at
/// the next tick: an exception escaping a BackgroundService would stop the whole host, and an
/// unreachable database for a moment must not take the API down with it.
/// </summary>
public sealed class ExpiredSessionPurger(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<AuthSessionOptions> options,
    ILogger<ExpiredSessionPurger> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.PurgeIntervalMinutes), time);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await PurgeOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task PurgeOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var purged = await scope.ServiceProvider.GetRequiredService<SessionService>().PurgeExpiredAsync(ct);
            if (purged > 0)
            {
                logger.LogInformation("Purged {Count} expired sessions.", purged);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Purging expired sessions failed; will retry at the next interval.");
        }
    }
}
