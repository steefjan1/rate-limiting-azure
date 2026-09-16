using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using RateLimiting.Api;
using RateLimiting.Api.Distributed;
using RateLimiting.Api.Limiters;

var builder = WebApplication.CreateBuilder(args);

var limits = builder.Configuration.GetSection("RateLimits").Get<RateLimitSettings>() ?? new();
var window = TimeSpan.FromMilliseconds(limits.WindowMs);

// Who is being limited? Here: the caller's X-Client-Id header (APIM sets it from the
// subscription). Fall back to the remote IP. The partition key is the most important
// design decision in the whole file, and most explanations of rate limiting skip it.
static string PartitionKey(HttpContext ctx) =>
    ctx.Request.Headers.TryGetValue("X-Client-Id", out var id) && !string.IsNullOrWhiteSpace(id)
        ? id.ToString()
        : ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

// Connect to Redis (if configured) while services can still be registered. The
// AddRateLimiter callback below runs lazily, after the container is built.
var redisDb = RedisLimiters.TryConnect(builder);
var redisEnabled = redisDb is not null;

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // One rejection handler for all six: 429 + Retry-After + problem details.
    // "Request rejected" is not the end of the story. The client needs to know when to come back.
    options.OnRejected = async (context, ct) =>
    {
        var response = context.HttpContext.Response;
        var policy = context.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global";
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.Headers.Append("X-RateLimit-Policy", policy);

        double? retryAfterSeconds = null;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            retryAfterSeconds = Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.RetryAfter = retryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
        }

        // One structured line per rejection. Container Apps ships stdout to Log Analytics
        // (ContainerAppConsoleLogs_CL), where docs/kql.md parses it. Keep the key=value shape.
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RateLimiting")
            .LogWarning("RateLimitRejected policy={Policy} client={Client} path={Path} retryAfterSeconds={RetryAfter}",
                policy, PartitionKey(context.HttpContext), context.HttpContext.Request.Path, retryAfterSeconds ?? 0);

        await response.WriteAsJsonAsync(new
        {
            type = "https://httpwg.org/specs/rfc6585.html#status-429",
            title = "Too Many Requests",
            status = 429,
            retryAfterSeconds,
        }, ct);
    };

    // 1. Fixed window counter (built in). Simple, but a burst that straddles the boundary
    //    gets 2x the limit through. The load test shows it.
    options.AddPolicy("fixed-window", ctx => RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ =>
        new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = window,
            QueueLimit = 0,
        }));

    // 2. Sliding window log (custom). Exact; memory grows with PermitLimit per partition.
    options.AddPolicy("sliding-log", ctx => RateLimitPartition.Get(PartitionKey(ctx), _ =>
        new SlidingWindowLogRateLimiter(new SlidingWindowLogOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = window,
        })));

    // 3. Sliding window counter (built in). .NET's version is segmented rather than the
    //    weighted two-window approximation most explanations describe; same idea, same trade.
    options.AddPolicy("sliding-counter", ctx => RateLimitPartition.GetSlidingWindowLimiter(PartitionKey(ctx), _ =>
        new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = window,
            SegmentsPerWindow = limits.SlidingWindowSegments,
            QueueLimit = 0,
        }));

    // 4. Token bucket (built in). Capacity = PermitLimit, refilled one token at a time,
    //    so a full bucket can absorb a burst and the average rate stays at the limit.
    options.AddPolicy("token-bucket", ctx => RateLimitPartition.GetTokenBucketLimiter(PartitionKey(ctx), _ =>
        new TokenBucketRateLimiterOptions
        {
            TokenLimit = limits.PermitLimit,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(limits.WindowMs / (double)limits.PermitLimit),
            AutoReplenishment = true,
            QueueLimit = 0,
        }));

    // 5. Leaky bucket (custom). Drains at a fixed rate; early arrivals wait, overflow is rejected.
    options.AddPolicy("leaky-bucket", ctx => RateLimitPartition.Get(PartitionKey(ctx), _ =>
        new LeakyBucketRateLimiter(new LeakyBucketOptions
        {
            PermitLimit = limits.PermitLimit,
            Window = window,
            QueueLimit = limits.LeakyBucketQueueLimit,
        })));

    // 6. Concurrency limiter (built in). Not about time at all: how many are in flight.
    options.AddPolicy("concurrency", ctx => RateLimitPartition.GetConcurrencyLimiter(PartitionKey(ctx), _ =>
        new ConcurrencyLimiterOptions
        {
            PermitLimit = limits.MaxConcurrency,
            QueueLimit = 0,
        }));

    // Distributed variants, only when ConnectionStrings:Redis is set.
    if (redisDb is not null) RedisLimiters.AddPolicies(options, redisDb, limits, PartitionKey);
});

var app = builder.Build();
app.UseRateLimiter();

var api = app.MapGroup("/api");
var ok = (HttpContext ctx) => Results.Ok(new
{
    algorithm = ctx.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "none",
    client = PartitionKey(ctx),
    instance = Environment.MachineName,
    at = DateTimeOffset.UtcNow,
});

api.MapGet("/unlimited", ok);
api.MapGet("/fixed-window", ok).RequireRateLimiting("fixed-window");
api.MapGet("/sliding-log", ok).RequireRateLimiting("sliding-log");
api.MapGet("/sliding-counter", ok).RequireRateLimiting("sliding-counter");
api.MapGet("/token-bucket", ok).RequireRateLimiting("token-bucket");
api.MapGet("/leaky-bucket", ok).RequireRateLimiting("leaky-bucket");

// The concurrency endpoint simulates a slow downstream, because that is the only
// situation in which a concurrency limit is the right tool.
api.MapGet("/concurrency", async (HttpContext ctx) =>
{
    await Task.Delay(limits.SlowBackendMs, ctx.RequestAborted);
    return ok(ctx);
}).RequireRateLimiting("concurrency");

if (redisEnabled)
{
    var redis = api.MapGroup("/redis");
    redis.MapGet("/fixed-window", ok).RequireRateLimiting(RedisLimiters.FixedWindow);
    redis.MapGet("/sliding-log", ok).RequireRateLimiting(RedisLimiters.SlidingWindowLog);
    redis.MapGet("/sliding-counter", ok).RequireRateLimiting(RedisLimiters.SlidingWindowCounter);
    redis.MapGet("/token-bucket", ok).RequireRateLimiting(RedisLimiters.TokenBucket);
}
else
{
    api.MapGet("/redis/{algorithm}", (string algorithm) =>
        Results.Problem(statusCode: 501, title: "Redis not configured",
            detail: $"Set ConnectionStrings:Redis to enable the distributed '{algorithm}' limiter."));
}

app.MapGet("/", () => Results.Ok(new
{
    name = "rate-limiting-azure",
    limits,
    redisEnabled,
    endpoints = new[]
    {
        "/api/unlimited", "/api/fixed-window", "/api/sliding-log", "/api/sliding-counter",
        "/api/token-bucket", "/api/leaky-bucket", "/api/concurrency",
        "/api/redis/fixed-window", "/api/redis/sliding-log", "/api/redis/sliding-counter", "/api/redis/token-bucket",
    },
}));
app.MapGet("/health", () => Results.Ok("healthy"));

app.Run();
