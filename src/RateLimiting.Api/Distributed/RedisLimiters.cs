using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using RateLimiting.Api;
using StackExchange.Redis;

namespace RateLimiting.Api.Distributed;

/// <summary>
/// Wires the Redis-backed policies. Kept in one file so the rest of the API has no
/// dependency on StackExchange.Redis: delete this folder and the package reference
/// and the six in-process algorithms still build and run.
///
/// Two steps on purpose. <see cref="TryConnect"/> runs while services can still be
/// registered. <see cref="AddPolicies"/> runs inside the AddRateLimiter callback, which
/// executes lazily after the container is built, when the service collection is read-only.
/// </summary>
public static class RedisLimiters
{
    public const string FixedWindow = "redis-fixed";
    public const string SlidingWindowLog = "redis-sliding-log";
    public const string SlidingWindowCounter = "redis-sliding-counter";
    public const string TokenBucket = "redis-token-bucket";

    /// <returns>The Redis database when ConnectionStrings:Redis is configured, otherwise null.</returns>
    public static IDatabase? TryConnect(WebApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connectionString)) return null;

        var mux = ConnectionMultiplexer.Connect(connectionString);
        builder.Services.AddSingleton<IConnectionMultiplexer>(mux);
        return mux.GetDatabase();
    }

    public static void AddPolicies(RateLimiterOptions options, IDatabase db, RateLimitSettings limits, Func<HttpContext, string> partitionKey)
    {
        AddPolicy(FixedWindow, RedisAlgorithm.FixedWindow);
        AddPolicy(SlidingWindowLog, RedisAlgorithm.SlidingWindowLog);
        AddPolicy(SlidingWindowCounter, RedisAlgorithm.SlidingWindowCounter);
        AddPolicy(TokenBucket, RedisAlgorithm.TokenBucket);

        void AddPolicy(string name, RedisAlgorithm algorithm) =>
            options.AddPolicy(name, ctx => RateLimitPartition.Get(partitionKey(ctx), key =>
                new RedisRateLimiter(db, key, algorithm, new RedisLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromMilliseconds(limits.WindowMs),
                    FailOpen = limits.RedisFailOpen,
                })));
    }
}
