namespace DataScope.Worker;

/// <summary>
/// Holds a loop to at most <c>perSecond</c> steps per second on average, so a scan can be kept gentle on a busy
/// file server. Up to one second of unused allowance can be spent at once, so short pauses (a slow folder, a
/// progress report) do not make the scan crawl afterwards, but a long pause never turns into a burst.
/// A rate of zero or less means no limit.
/// </summary>
public sealed class Pacer(int perSecond, TimeProvider time)
{
    private readonly TimeSpan _interval = perSecond > 0 ? TimeSpan.FromSeconds(1.0 / perSecond) : TimeSpan.Zero;
    private TimeSpan _next = TimeSpan.MinValue;

    public bool Enabled => _interval > TimeSpan.Zero;

    /// <summary>Records one step at <paramref name="now"/> and returns how long to wait before taking it.</summary>
    public TimeSpan Next(TimeSpan now)
    {
        if (!Enabled)
        {
            return TimeSpan.Zero;
        }
        // The first step starts with no allowance; after that, at most one second of it can build up.
        var earliest = _next == TimeSpan.MinValue ? now : now - TimeSpan.FromSeconds(1);
        if (_next < earliest)
        {
            _next = earliest;
        }
        var wait = _next > now ? _next - now : TimeSpan.Zero;
        _next += _interval;
        return wait;
    }

    public Task WaitAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }
        var wait = Next(time.GetElapsedTime(0));
        return wait > TimeSpan.Zero ? Task.Delay(wait, time, ct) : Task.CompletedTask;
    }
}
