using System.Globalization;
using System.Net;
using System.Text.Json;
using Octokit;

namespace abaci_bot.Services;

/// <summary>
/// Exception thrown when a pipeline execution fails due to a non-retriable external error or
/// invalid remote state so the dispatcher aborts retries immediately and releases the delivery claim.
/// </summary>
public sealed class NonRetriablePipelineException : Exception
{
    public NonRetriablePipelineException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Authoritative classifier for GitHub API rate limits and retriable vs. non-retriable failures.
/// Shared across contexts, modules, pipelines, webhook dispatch, and background services to avoid
/// divergent classification rules and upward layer dependencies.
/// </summary>
internal static class TransientErrorPolicy
{
    public static bool TryGetRateLimitInfo(
        Exception ex,
        out TimeSpan? retryAfter,
        out string rateLimitType) =>
        TryGetRateLimitInfoCore(ex, depth: 0, out retryAfter, out rateLimitType);

    private static bool TryGetRateLimitInfoCore(
        Exception ex,
        int depth,
        out TimeSpan? retryAfter,
        out string rateLimitType)
    {
        if (depth > 8)
        {
            rateLimitType = string.Empty;
            retryAfter = null;
            return false;
        }

        if (ex is AggregateException agg)
        {
            TimeSpan? bestDelay = null;
            string? firstType = null;
            foreach (var inner in agg.Flatten().InnerExceptions)
            {
                if (TryGetRateLimitInfoCore(inner, depth + 1, out var innerDelay, out var innerType))
                {
                    firstType ??= innerType;
                    if (innerDelay.HasValue && (!bestDelay.HasValue || innerDelay.Value > bestDelay.Value))
                    {
                        bestDelay = innerDelay;
                    }
                }
            }

            if (firstType != null)
            {
                rateLimitType = firstType;
                retryAfter = bestDelay;
                return true;
            }
        }

        if (ex is RateLimitExceededException rle)
        {
            rateLimitType = "Core";
            DateTimeOffset? reset = null;
            try { reset = rle.Reset; } catch (NullReferenceException) { }
            var remaining = reset.HasValue ? reset.Value - DateTimeOffset.UtcNow : (TimeSpan?)null;
            retryAfter = remaining.HasValue && remaining.Value > TimeSpan.Zero ? remaining.Value : null;
            return true;
        }

        if (ex is AbuseException abuse)
        {
            rateLimitType = "Abuse";
            int? seconds = null;
            try { seconds = abuse.RetryAfterSeconds; } catch (NullReferenceException) { }
            retryAfter = seconds.HasValue
                ? TimeSpan.FromSeconds(Math.Max(60, seconds.Value))
                : TimeSpan.FromSeconds(60);
            return true;
        }

        if (ex is SecondaryRateLimitExceededException)
        {
            rateLimitType = "Secondary";
            retryAfter = TimeSpan.FromSeconds(60);
            return true;
        }

        if (ex is ApiException { StatusCode: HttpStatusCode.TooManyRequests } or
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
        {
            rateLimitType = "HTTP 429";
            retryAfter = TimeSpan.FromSeconds(60);
            return true;
        }

        if (ex is ApiException { StatusCode: HttpStatusCode.Forbidden, HttpResponse.Headers: { } headers })
        {
            if (TryGetHeaderValue(headers, "Retry-After", out var retryAfterRaw))
            {
                rateLimitType = "Secondary (Retry-After)";
                retryAfter = ParseRetryAfterHeader(retryAfterRaw);
                return true;
            }

            if (TryGetHeaderValue(headers, "X-RateLimit-Remaining", out var remainingRaw) &&
                string.Equals(remainingRaw.Trim(), "0", StringComparison.Ordinal))
            {
                rateLimitType = "Core (Header)";
                retryAfter = TryGetHeaderValue(headers, "X-RateLimit-Reset", out var resetRaw)
                    ? ParseRateLimitResetHeader(resetRaw)
                    : TimeSpan.FromSeconds(60);
                return true;
            }
        }

        if (ex is not AggregateException &&
            ex.InnerException is { } innerException &&
            !ReferenceEquals(innerException, ex))
        {
            return TryGetRateLimitInfoCore(innerException, depth + 1, out retryAfter, out rateLimitType);
        }

        rateLimitType = string.Empty;
        retryAfter = null;
        return false;
    }

    private static bool TryGetHeaderValue(
        IReadOnlyDictionary<string, string> headers,
        string headerName,
        out string value)
    {
        foreach (var kvp in headers)
        {
            if (string.Equals(kvp.Key, headerName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(kvp.Value))
            {
                value = kvp.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static TimeSpan ParseRetryAfterHeader(string raw)
    {
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var targetTime))
        {
            var delta = targetTime - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                return delta;
            }
        }

        return TimeSpan.FromSeconds(60);
    }

    private static TimeSpan ParseRateLimitResetHeader(string raw)
    {
        if (long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds))
        {
            var delta = DateTimeOffset.FromUnixTimeSeconds(epochSeconds) - DateTimeOffset.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                return delta;
            }
        }

        return TimeSpan.FromSeconds(60);
    }

    public static bool IsRateLimitOrAbuse(Exception ex) =>
        TryGetRateLimitInfo(ex, out _, out _);

    public static bool IsNonRetriable(Exception ex)
    {
        // Primary, secondary, abuse, and HTTP 429 rate limits are always transient.
        if (IsRateLimitOrAbuse(ex))
            return false;

        if (ex is NonRetriablePipelineException)
            return true;

        if (ex is AggregateException agg)
        {
            var innerExceptions = agg.Flatten().InnerExceptions;
            return innerExceptions.Count > 0 && innerExceptions.All(IsNonRetriable);
        }

        // Programming, configuration, and serialization faults are non-retriable.
        if (ex is ArgumentException or NotSupportedException or JsonException)
            return true;

        // Specific Octokit client error types (excluding rate-limit subclasses handled above).
        if (ex is AuthorizationException or ForbiddenException or NotFoundException)
            return true;

        if (ex is ApiException api)
            return IsNonRetriableHttpStatusCode((int)api.StatusCode);

        if (ex is HttpRequestException { StatusCode: { } statusCode })
            return IsNonRetriableHttpStatusCode((int)statusCode);

        return false;
    }

    private static bool IsNonRetriableHttpStatusCode(int statusCode) =>
        statusCode is >= 400 and < 500 and not (408 or 425 or 429);
}
