namespace BudgetTracker.Api.Tests.Database;

/// <summary>
/// Retries an operation that fails for transient environmental reasons, such as the container
/// runtime still releasing a port from the previous test run. Used only around starting the
/// database container: a failure after it is up is a real failure and is not retried.
/// </summary>
public static class Retry
{
    public static async Task RunAsync(
        Func<int, Task> attempt,
        int attempts,
        Func<int, TimeSpan> delayAfter,
        Func<TimeSpan, Task>? wait = null)
    {
        wait ??= delay => Task.Delay(delay);
        var failures = new List<Exception>();

        for (var number = 1; number <= attempts; number++)
        {
            try
            {
                await attempt(number);
                return;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                if (number < attempts)
                {
                    await wait(delayAfter(number));
                }
            }
        }

        // Every failure is kept: the last one alone can hide what went wrong first.
        throw new AggregateException($"Failed after {attempts} attempts.", failures);
    }
}
