using System.Threading.RateLimiting;

namespace RateLimiting.Api.Limiters;

/// <summary>
/// Sliding window log: remembers the timestamp of every accepted request and
/// allows a new one only when fewer than <see cref="SlidingWindowLogOptions.PermitLimit"/>
/// timestamps fall inside the trailing window.
///
/// Exact by construction (no window boundary, no approximation), but memory grows
/// with PermitLimit per partition. Precision costs memory.
/// .NET does not ship this one, so this is a custom <see cref="RateLimiter"/>.
/// </summary>
public sealed class SlidingWindowLogRateLimiter : RateLimiter
{
    private readonly SlidingWindowLogOptions _options;
    private readonly Queue<long> _log = new();
    private readonly object _gate = new();
    private long _lastActivityTicks = Environment.TickCount64;
    private long _rejected;

    public SlidingWindowLogRateLimiter(SlidingWindowLogOptions options)
    {
        if (options.PermitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(options.PermitLimit));
        if (options.Window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.Window));
        _options = options;
    }

    public override TimeSpan? IdleDuration =>
        TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            Evict(Environment.TickCount64);
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _options.PermitLimit - _log.Count,
                TotalFailedLeases = _rejected,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        if (permitCount > _options.PermitLimit)
            throw new ArgumentOutOfRangeException(nameof(permitCount));

        var now = Environment.TickCount64;
        lock (_gate)
        {
            _lastActivityTicks = now;
            Evict(now);

            if (_log.Count + permitCount <= _options.PermitLimit)
            {
                for (var i = 0; i < permitCount; i++) _log.Enqueue(now);
                return SimpleLease.Acquired;
            }

            _rejected++;
            // The oldest entry decides when a slot frees up again.
            var oldest = _log.Peek();
            var retryAfterMs = Math.Max(1, oldest + (long)_options.Window.TotalMilliseconds - now);
            return new SimpleLease(false, TimeSpan.FromMilliseconds(retryAfterMs));
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
        => new(AttemptAcquireCore(permitCount)); // no queueing in this implementation

    private void Evict(long now)
    {
        var cutoff = now - (long)_options.Window.TotalMilliseconds;
        while (_log.Count > 0 && _log.Peek() <= cutoff) _log.Dequeue();
    }
}

public sealed class SlidingWindowLogOptions
{
    public int PermitLimit { get; set; } = 10;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);
}
