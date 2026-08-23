using AutoCoder.Abstractions.Config;
using AutoCoder.Core.Runs;

namespace AutoCoder.Tests;

/// <summary>
/// RunConcurrency is a process-wide static gate, so these tests share state with any other
/// caller of Configure() in this test run. Only this file calls it with MaxConcurrentRuns > 0
/// (the first call wins for the process lifetime — see RunConcurrency.Configure).
/// </summary>
public sealed class RunConcurrencyTests
{
    [Fact]
    public async Task Acquire_blocks_beyond_the_configured_limit_until_a_slot_is_released()
    {
        RunConcurrency.Configure(new LimitsOptions { MaxConcurrentRuns = 1 });

        using var first = await RunConcurrency.AcquireAsync(CancellationToken.None);

        var secondTask = RunConcurrency.AcquireAsync(CancellationToken.None);
        var completedEarly = await Task.WhenAny(secondTask, Task.Delay(200));
        Assert.NotEqual(secondTask, completedEarly);

        first.Dispose();
        var second = await secondTask;
        second.Dispose();
    }

    [Fact]
    public async Task Configure_is_one_shot_a_later_call_with_a_higher_limit_has_no_effect()
    {
        RunConcurrency.Configure(new LimitsOptions { MaxConcurrentRuns = 1 });
        // Second call requests a much higher cap. If Configure() were not one-shot, this
        // would let two runs proceed concurrently below — it must not.
        RunConcurrency.Configure(new LimitsOptions { MaxConcurrentRuns = 99 });

        using var first = await RunConcurrency.AcquireAsync(CancellationToken.None);
        var secondTask = RunConcurrency.AcquireAsync(CancellationToken.None);
        var completedEarly = await Task.WhenAny(secondTask, Task.Delay(200));
        Assert.NotEqual(secondTask, completedEarly);

        first.Dispose();
        (await secondTask).Dispose();
    }
}
