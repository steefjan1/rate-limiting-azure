using System.Threading.RateLimiting;

namespace RateLimiting.Api.Limiters;

/// <summary>
/// Minimal lease used by the custom limiters. Carries an optional Retry-After hint
/// so the shared OnRejected handler can surface it as an HTTP header.
/// </summary>
public sealed class SimpleLease : RateLimitLease
{
    public static readonly SimpleLease Acquired = new(true, null);

    private readonly TimeSpan? _retryAfter;
    private readonly Action? _onDispose;

    public SimpleLease(bool isAcquired, TimeSpan? retryAfter, Action? onDispose = null)
    {
        IsAcquired = isAcquired;
        _retryAfter = retryAfter;
        _onDispose = onDispose;
    }

    public override bool IsAcquired { get; }

    public override IEnumerable<string> MetadataNames =>
        _retryAfter is null ? Array.Empty<string>() : new[] { MetadataName.RetryAfter.Name };

    public override bool TryGetMetadata(string metadataName, out object? metadata)
    {
        if (metadataName == MetadataName.RetryAfter.Name && _retryAfter is { } ra)
        {
            metadata = ra;
            return true;
        }
        metadata = null;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _onDispose?.Invoke();
    }
}
