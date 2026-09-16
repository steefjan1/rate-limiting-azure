using System.Threading.RateLimiting;
using RateLimiting.Api.Limiters;
using StackExchange.Redis;

namespace RateLimiting.Api.Distributed;

/// <summary>
/// One <see cref="RateLimiter"/> that runs the counting logic as a Lua script inside
/// Redis, so every replica of this API shares the same counter. This is what the
/// in-process limiters cannot give you once you scale past one instance.
///
/// Each script is atomic (Redis runs scripts single-threaded) and returns
/// <c>{allowed, retryAfterMs}</c>. The client supplies "now" so the script stays
/// deterministic and replication-safe.
/// </summary>
public sealed class RedisRateLimiter : RateLimiter
{
    private readonly IDatabase _db;
    private readonly RedisKey _key;
    private readonly RedisAlgorithm _algorithm;
    private readonly RedisLimiterOptions _options;
    private long _lastActivityTicks = Environment.TickCount64;
    private long _rejected;

    public RedisRateLimiter(IDatabase db, string partitionKey, RedisAlgorithm algorithm, RedisLimiterOptions options)
    {
        _db = db;
        _algorithm = algorithm;
        _options = options;
        // Hash tag {..} keeps every key of a partition in the same cluster slot.
        // Azure Managed Redis is clustered by default, and the sliding counter
        // script touches two keys.
        _key = $"{{rl:{algorithm}:{partitionKey}}}";
    }

    public override TimeSpan? IdleDuration =>
        TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));

    public override RateLimiterStatistics? GetStatistics() => new()
    {
        TotalFailedLeases = Interlocked.Read(ref _rejected),
    };

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
        => AcquireAsyncCore(permitCount, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = (long)_options.Window.TotalMilliseconds;

        var (script, args) = _algorithm switch
        {
            RedisAlgorithm.FixedWindow => (Scripts.FixedWindow,
                new RedisValue[] { _options.PermitLimit, windowMs }),

            RedisAlgorithm.SlidingWindowLog => (Scripts.SlidingWindowLog,
                new RedisValue[] { _options.PermitLimit, windowMs, nowMs, Guid.NewGuid().ToString("N") }),

            RedisAlgorithm.SlidingWindowCounter => (Scripts.SlidingWindowCounter,
                new RedisValue[] { _options.PermitLimit, windowMs, nowMs }),

            RedisAlgorithm.TokenBucket => (Scripts.TokenBucket,
                new RedisValue[] { _options.PermitLimit, (double)_options.PermitLimit / windowMs, nowMs, permitCount }),

            _ => throw new ArgumentOutOfRangeException(nameof(_algorithm)),
        };

        RedisResult result;
        try
        {
            result = await _db.ScriptEvaluateAsync(script, new[] { _key }, args).ConfigureAwait(false);
        }
        catch (RedisException) when (_options.FailOpen)
        {
            // Redis unavailable: a rate limiter should not take the API down with it.
            // Failing open is a deliberate choice; log it in real code and alert on it.
            return SimpleLease.Acquired;
        }

        var values = (RedisValue[])result!;
        var allowed = (long)values[0] == 1;
        if (allowed) return SimpleLease.Acquired;

        Interlocked.Increment(ref _rejected);
        var retryMs = Math.Max(1, (long)values[1]);
        return new SimpleLease(false, TimeSpan.FromMilliseconds(retryMs));
    }

    private static class Scripts
    {
        // ARGV: limit, windowMs
        public const string FixedWindow = """
            local count = redis.call('INCR', KEYS[1])
            if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[2]) end
            if count > tonumber(ARGV[1]) then
              return {0, redis.call('PTTL', KEYS[1])}
            end
            return {1, 0}
            """;

        // ARGV: limit, windowMs, nowMs, member
        public const string SlidingWindowLog = """
            local limit = tonumber(ARGV[1]); local window = tonumber(ARGV[2]); local now = tonumber(ARGV[3])
            redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, now - window)
            local count = redis.call('ZCARD', KEYS[1])
            if count < limit then
              redis.call('ZADD', KEYS[1], now, ARGV[4])
              redis.call('PEXPIRE', KEYS[1], window)
              return {1, 0}
            end
            local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
            return {0, tonumber(oldest[2]) + window - now}
            """;

        // ARGV: limit, windowMs, nowMs
        // Two fixed counters (current and previous window); the previous one is
        // weighted by how much of it still overlaps the trailing window.
        public const string SlidingWindowCounter = """
            local limit = tonumber(ARGV[1]); local window = tonumber(ARGV[2]); local now = tonumber(ARGV[3])
            local curId = math.floor(now / window)
            local curKey = KEYS[1] .. ':' .. curId
            local prevKey = KEYS[1] .. ':' .. (curId - 1)
            local cur = tonumber(redis.call('GET', curKey) or '0')
            local prev = tonumber(redis.call('GET', prevKey) or '0')
            local elapsed = (now % window) / window
            local weighted = prev * (1 - elapsed) + cur
            if weighted + 1 > limit then
              return {0, window - (now % window)}
            end
            redis.call('INCR', curKey)
            redis.call('PEXPIRE', curKey, window * 2)
            return {1, 0}
            """;

        // ARGV: capacity, refillTokensPerMs, nowMs, cost
        public const string TokenBucket = """
            local capacity = tonumber(ARGV[1]); local rate = tonumber(ARGV[2])
            local now = tonumber(ARGV[3]); local cost = tonumber(ARGV[4])
            local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
            local tokens = tonumber(data[1]); local ts = tonumber(data[2])
            if tokens == nil then tokens = capacity; ts = now end
            tokens = math.min(capacity, tokens + math.max(0, now - ts) * rate)
            local allowed = 0; local retry = 0
            if tokens >= cost then
              tokens = tokens - cost; allowed = 1
            else
              retry = math.ceil((cost - tokens) / rate)
            end
            redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', now)
            redis.call('PEXPIRE', KEYS[1], math.ceil(capacity / rate) * 2)
            return {allowed, retry}
            """;
    }
}

public enum RedisAlgorithm
{
    FixedWindow,
    SlidingWindowLog,
    SlidingWindowCounter,
    TokenBucket,
}

public sealed class RedisLimiterOptions
{
    /// <summary>Requests per window (or bucket capacity for the token bucket, refilled once per window).</summary>
    public int PermitLimit { get; set; } = 10;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Allow traffic through when Redis is unreachable. Default true; see the post for the trade-off.</summary>
    public bool FailOpen { get; set; } = true;
}
