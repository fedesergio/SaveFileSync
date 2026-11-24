namespace SaveFileSync.Metadata;

public enum MetadataFailure
{
    /// <summary>The key was rejected (401/403) or is missing.</summary>
    InvalidKey,

    /// <summary>The service asked us to slow down (429).</summary>
    RateLimited,

    /// <summary>No connection, timeout or a server error: try again later.</summary>
    Unavailable,

    /// <summary>The answer was not what the documented API returns.</summary>
    BadResponse,
}

/// <summary>A failure of an online metadata source, classified so that callers know whether to retry.</summary>
public sealed class MetadataException(MetadataFailure failure, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public MetadataFailure Failure { get; } = failure;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
