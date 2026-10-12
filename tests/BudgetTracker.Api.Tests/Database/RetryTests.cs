namespace BudgetTracker.Api.Tests.Database;

public class RetryTests
{
    private static readonly Func<TimeSpan, Task> NoWait = _ => Task.CompletedTask;

    [Fact]
    public async Task An_operation_that_works_runs_once()
    {
        var calls = 0;

        await Retry.RunAsync(_ => { calls++; return Task.CompletedTask; }, attempts: 4, _ => TimeSpan.Zero, NoWait);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_until_it_succeeds()
    {
        var calls = 0;

        await Retry.RunAsync(
            _ =>
            {
                calls++;
                return calls < 3 ? Task.FromException(new InvalidOperationException($"failure {calls}")) : Task.CompletedTask;
            },
            attempts: 4, _ => TimeSpan.Zero, NoWait);

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task It_gives_up_after_the_last_attempt_and_reports_every_failure()
    {
        var calls = 0;

        var exception = await Assert.ThrowsAsync<AggregateException>(() => Retry.RunAsync(
            _ => Task.FromException(new InvalidOperationException($"failure {++calls}")),
            attempts: 3, _ => TimeSpan.Zero, NoWait));

        Assert.Equal(3, calls);
        Assert.Equal(
            ["failure 1", "failure 2", "failure 3"],
            exception.InnerExceptions.Select(e => e.Message).ToArray());
    }

    [Fact]
    public async Task Each_attempt_is_told_its_number_starting_from_one()
    {
        var seen = new List<int>();

        await Assert.ThrowsAsync<AggregateException>(() => Retry.RunAsync(
            number => { seen.Add(number); return Task.FromException(new Exception()); },
            attempts: 3, _ => TimeSpan.Zero, NoWait));

        Assert.Equal([1, 2, 3], seen);
    }

    [Fact]
    public async Task It_waits_between_attempts_but_not_after_the_last_one()
    {
        var waits = new List<TimeSpan>();

        await Assert.ThrowsAsync<AggregateException>(() => Retry.RunAsync(
            _ => Task.FromException(new Exception()),
            attempts: 3,
            delayAfter: number => TimeSpan.FromSeconds(number * 10),
            wait: delay => { waits.Add(delay); return Task.CompletedTask; }));

        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)], waits);
    }

    [Fact]
    public async Task A_success_after_failures_does_not_wait_again()
    {
        var waits = 0;
        var calls = 0;

        await Retry.RunAsync(
            _ => ++calls < 2 ? Task.FromException(new Exception()) : Task.CompletedTask,
            attempts: 4, _ => TimeSpan.FromSeconds(1), wait: _ => { waits++; return Task.CompletedTask; });

        Assert.Equal(1, waits);
    }
}
