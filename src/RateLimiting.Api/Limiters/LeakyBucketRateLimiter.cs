using System.Threading.RateLimiting;

namespace RateLimiting.Api.Limiters;

/// <summary>
/// Leaky bucket, queue flavour: requests drain at a fixed rate (one every
/// <c>Window / PermitLimit</c>). A request that arrives early is held until its
/// drain slot; a request that would have to wait longer than the queue allows is
/// rejected with a Retry-After hint.
///
/// Implemented as "virtual scheduling" (the same math as GCRA): we only track the
/// timestamp of the next free drain slot, so it costs one long per partition.
/// This is the algorithm that smooths throughput toward the backend at the price
/// of added latency for the client, which is exactly why it is rarely what you want
/// at an HTTP edge and often what you want in front of a fragile downstream.
/// .NET does not ship this one either.
/// </summary>
public sealed class LeakyBucketRateLimiter : RateLimiter
{
    private readonly LeakyBucketOptions _options;
    private readonly double _drainIntervalMs;
    private readonly double _maxQueueDelayMs;
    private readonly object _gate = new();
    private double _nextFreeSlotMs;
    private long _lastActivityTicks = Environment.TickCount64;
    private long _rejected;
    private int _queued;

    public LeakyBucketRateLimiter(LeakyBucketOptions options)
    {
        if (options.PermitLimit <= 0) throw new ArgumentOutOfRangeException(nameof(options.PermitLimit));
        if (options.Window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options.Window));
        if (options.QueueLimit < 0) throw new ArgumentOutOfRangeException(nameof(options.QueueLimit));

        _options = options;
        _drainIntervalMs = options.Window.TotalMilliseconds / options.PermitLimit;
        _maxQueueDelayMs = _drainIntervalMs * options.QueueLimit;
        _nextFreeSlotMs = Environment.TickCount64;
    }

    public override TimeSpan? IdleDuration =>
        TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));

    public override RateLimiterStatistics? GetStatistics() => new()
    {
        CurrentQueuedCount = Volatile.Read(ref _queued),
        TotalFailedLeases = Interlocked.Read(ref _rejected),
    };

    /// <summary>Synchronous attempt: only succeeds when a drain slot is free right now.</summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var (waitMs, retryAfter) = Reserve(permitCount, allowQueue: false);
        return waitMs == 0 && retryAfter is null
            ? SimpleLease.Acquired
            : new SimpleLease(false, retryAfter);
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var (waitMs, retryAfter) = Reserve(permitCount, allowQueue: true);
        if (retryAfter is not null) return new SimpleLease(false, retryAfter);
        if (waitMs == 0) return SimpleLease.Acquired;

        Interlocked.Increment(ref _queued);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(waitMs), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
        return SimpleLease.Acquired;
    }

    /// <returns>waitMs to hold the request, or a Retry-After when it cannot be queued.</returns>
    private (long waitMs, TimeSpan? retryAfter) Reserve(int permitCount, bool allowQueue)
    {
        if (permitCount <= 0) throw new ArgumentOutOfRangeException(nameof(permitCount));

        var now = (double)Environment.TickCount64;
        lock (_gate)
        {
            _lastActivityTicks = (long)now;

            // Bucket drained completely while idle: the next slot is "now".
            if (_nextFreeSlotMs < now) _nextFreeSlotMs = now;

            var slot = _nextFreeSlotMs;
            var wait = slot - now;
            var maxWait = allowQueue ? _maxQueueDelayMs : 0;

            if (wait > maxWait)
            {
                _rejected++;
                // Earliest time a queue position frees up.
                var retryMs = Math.Max(1, wait - maxWait);
                return (0, TimeSpan.FromMilliseconds(retryMs));
            }

            _nextFreeSlotMs = slot + _drainIntervalMs * permitCount;
            return ((long)Math.Ceiling(wait), null);
        }
    }
}

public sealed class LeakyBucketOptions
{
    /// <summary>Requests drained per <see cref="Window"/>.</summary>
    public int PermitLimit { get; set; } = 10;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How many requests may wait for a slot before we start rejecting.</summary>
    public int QueueLimit { get; set; } = 10;
}
