using BudgetTracker.Api.Auth;
using BudgetTracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Maintenance;

/// <summary>
/// One-shot commands for deployment, run as <c>BudgetTracker.Api.dll &lt;command&gt;</c> instead of
/// starting the web server. They exist so the schema is changed by an explicit deploy step with
/// its own database credentials, never by the running application.
/// </summary>
public static class MaintenanceCommands
{
    public const string Migrate = "migrate";
    public const string SeedDemo = "seed-demo";

    /// <summary>The first argument is a command when it is not a configuration switch such as --urls.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0 && !args[0].StartsWith('-');

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var log = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Maintenance");
        var db = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();

        switch (args[0])
        {
            case Migrate:
                var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
                await db.Database.MigrateAsync(ct);
                log.LogInformation("Applied {Count} migration(s): {Names}", pending.Count, string.Join(", ", pending));
                return 0;

            case SeedDemo:
                var password = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Demo:Password"];
                if (string.IsNullOrWhiteSpace(password))
                {
                    log.LogError("Set Demo:Password (the password the demo account will have).");
                    return 2;
                }

                await DemoData.ResetAsync(
                    db,
                    scope.ServiceProvider.GetRequiredService<IPasswordService>(),
                    scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                    password,
                    ct);
                log.LogInformation("Demo account {Email} reset.", DemoData.Email);
                return 0;

            default:
                log.LogError("Unknown command '{Command}'. Commands: {Migrate}, {SeedDemo}.", args[0], Migrate, SeedDemo);
                return 2;
        }
    }
}
