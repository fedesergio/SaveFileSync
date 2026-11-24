namespace SaveFileSync.Metadata;

/// <summary>Spaces requests at least <c>minInterval</c> apart (IGDB allows about 4 per second).</summary>
public sealed class RateLimiter(TimeSpan minInterval, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private long _nextTicks;

    public async Task WaitAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = Environment.TickCount64;
            var wait = _nextTicks - now;
            if (wait > 0)
            {
                await _delay(TimeSpan.FromMilliseconds(wait), token).ConfigureAwait(false);
            }

            _nextTicks = Math.Max(now, _nextTicks) + (long)minInterval.TotalMilliseconds;
        }
        finally
        {
            _gate.Release();
        }
    }
}
