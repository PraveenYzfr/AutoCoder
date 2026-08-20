using AutoCoder.Abstractions.Config;

namespace AutoCoder.Core.Runs;

/// <summary>Process-wide cap so overlapping webhooks cannot spawn unlimited pipelines.</summary>
public static class RunConcurrency
{
    private static readonly object Gate = new();
    private static SemaphoreSlim? _slots;
    private static int _max;

    public static void Configure(LimitsOptions limits)
    {
        var max = limits.MaxConcurrentRuns;
        lock (Gate)
        {
            if (_slots is not null)
                return;
            if (max <= 0)
                return;
            _max = max;
            _slots = new SemaphoreSlim(max, max);
            Console.WriteLine($"[limits] max_concurrent_runs={max}");
        }
    }

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var slots = _slots;
        if (slots is null)
            return Noop.Instance;

        Console.WriteLine($"[limits] waiting for run slot (max {_max})");
        await slots.WaitAsync(cancellationToken);
        return new Releaser(slots);
    }

    private sealed class Releaser(SemaphoreSlim slots) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                slots.Release();
        }
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
