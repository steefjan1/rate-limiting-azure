namespace RateLimiting.Api;

/// <summary>
/// One set of numbers shared by every algorithm so the load test compares like with like:
/// "PermitLimit requests per WindowMs" for the five time-based limiters, and
/// MaxConcurrency for the concurrency limiter.
/// </summary>
public sealed class RateLimitSettings
{
    public int PermitLimit { get; set; } = 10;
    public int WindowMs { get; set; } = 1000;

    /// <summary>Segments for the .NET sliding window counter. More segments = closer to the log.</summary>
    public int SlidingWindowSegments { get; set; } = 10;

    /// <summary>Leaky bucket: requests allowed to wait for a drain slot.</summary>
    public int LeakyBucketQueueLimit { get; set; } = 10;

    /// <summary>Concurrency limiter: simultaneous in-flight requests.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Simulated downstream latency for the concurrency endpoint.</summary>
    public int SlowBackendMs { get; set; } = 250;

    public bool RedisFailOpen { get; set; } = true;
}
