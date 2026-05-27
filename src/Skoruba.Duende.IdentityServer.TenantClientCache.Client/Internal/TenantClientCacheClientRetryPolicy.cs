// Feature: tenant-client-cache-public-read, Task 8
//
// Native (NOT Polly-based) retry decision + exponential backoff calculator
// for the SDK consumer. R10.1 forbids new third-party dependencies, so the
// SDK ships its own minimal retry primitive instead of taking a dependency
// on Polly / Microsoft.Extensions.Http.Resilience.
//
// Retry contract:
// - MaxAttempts is supplied by the caller; the policy NEVER retries past
//   that bound (R11.1).
// - HTTP status retry set: {500, 502, 503, 504} only (R11.1).
// - 4xx (400, 401, 403, 404, 405, 429) are NEVER retried (R11.2). They
//   propagate to the caller for fail-soft / surface-as-outcome handling.
// - Exception retry set: HttpRequestException, SocketException, and
//   TaskCanceledException whose InnerException is TimeoutException
//   (i.e. HttpClient.Timeout fired). Caller-driven cancellation
//   (CancellationToken cancelled by caller) MUST NOT be classified as
//   transient — that surfaces as TaskCanceledException without an inner
//   TimeoutException and the higher-level retry loop in
//   TenantClientCacheClient re-throws it (R11.5).
// - Backoff formula: baseDelay * 2^attempt, capped at 60 seconds (R11.3).
//   No jitter — deterministic so property-based tests can assert exact
//   delay values.
//
// Validates: Requirements 11.1, 11.2, 11.3, 11.5

#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

/// <summary>
/// Pure-function retry primitives used by <c>TenantClientCacheClient</c>
/// (Task 9). Owns no state; safe to register as a singleton.
/// </summary>
internal sealed class TenantClientCacheClientRetryPolicy
{
    /// <summary>
    /// Decide whether the next attempt should be retried, given the most
    /// recent HTTP status code.
    /// </summary>
    /// <param name="status">The status code returned by the just-completed HTTP call.</param>
    /// <param name="attempt">
    /// 0-based index of the attempt that just completed. <c>0</c> means
    /// "the initial call" — the next attempt would be attempt #1, the
    /// first retry.
    /// </param>
    /// <param name="maxAttempts">
    /// Maximum NUMBER OF RETRIES (excluding the initial call) the caller
    /// is willing to tolerate. Equivalent to
    /// <c>TenantClientCacheClientOptions.MaxRetryAttempts</c>.
    /// </param>
    /// <returns><see langword="true"/> iff the policy authorises another retry.</returns>
    public bool ShouldRetry(HttpStatusCode status, int attempt, int maxAttempts)
    {
        // R11.1: cap on retry budget.
        if (attempt >= maxAttempts) return false;

        // R11.1: retry only on the documented 5xx subset.
        // R11.2: 4xx (400, 401, 403, 404, 405, 429) NEVER retry — they
        // propagate so the SDK can surface them as discrete outcomes.
        return status is HttpStatusCode.InternalServerError      // 500
            or HttpStatusCode.BadGateway                          // 502
            or HttpStatusCode.ServiceUnavailable                  // 503
            or HttpStatusCode.GatewayTimeout;                     // 504
    }

    /// <summary>
    /// Compute the delay before the next retry attempt.
    /// </summary>
    /// <param name="attempt">
    /// 0-based index of the upcoming retry. <c>0</c> means
    /// "first retry after the initial call". Note the difference with
    /// <see cref="ShouldRetry"/>'s parameter: callers typically increment
    /// the local <c>attempt</c> counter between the <c>ShouldRetry</c>
    /// check and the <c>NextDelay</c> call.
    /// </param>
    /// <param name="baseDelay">
    /// Multiplicative base from <c>TenantClientCacheClientOptions.RetryBaseDelay</c>
    /// (default 200 ms).
    /// </param>
    /// <returns>
    /// <c>min(baseDelay * 2^attempt, 60 s)</c>. The cap protects callers
    /// against accidental misconfiguration (e.g. <c>baseDelay = 1 hour</c>)
    /// from producing absurd delays.
    /// </returns>
    public TimeSpan NextDelay(int attempt, TimeSpan baseDelay)
    {
        // R11.3: baseDelay * 2^attempt, deterministic (no jitter).
        if (baseDelay <= TimeSpan.Zero || attempt < 0)
            return TimeSpan.Zero;

        // Guard against shift overflow when callers pass an absurd attempt
        // count. Once 2^attempt would push the resulting ticks past Int64,
        // the exponent saturates the cap by definition.
        var cap = TimeSpan.FromMinutes(1).Ticks;
        if (attempt >= 62)
            return TimeSpan.FromTicks(cap);

        long shifted;
        try
        {
            shifted = checked(baseDelay.Ticks * (1L << attempt));
        }
        catch (OverflowException)
        {
            return TimeSpan.FromTicks(cap);
        }

        return TimeSpan.FromTicks(Math.Min(shifted, cap));
    }

    /// <summary>
    /// Classify whether <paramref name="ex"/> represents a transient
    /// network failure that the SDK should retry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HttpRequestException"/> is always transient (TCP reset,
    /// DNS hiccup, etc.).
    /// </para>
    /// <para>
    /// <see cref="SocketException"/> is always transient.
    /// </para>
    /// <para>
    /// <see cref="TaskCanceledException"/> is transient ONLY when its
    /// <see cref="Exception.InnerException"/> is
    /// <see cref="TimeoutException"/> — that is the shape produced by
    /// <c>HttpClient.Timeout</c> firing. A bare
    /// <see cref="TaskCanceledException"/> (no inner) means the caller's
    /// <see cref="System.Threading.CancellationToken"/> was triggered;
    /// the caller-level retry loop re-throws it (R11.5).
    /// </para>
    /// </remarks>
    public static bool IsTransientNetworkException(Exception ex)
    {
        if (ex is null) return false;

        if (ex is HttpRequestException) return true;
        if (ex is SocketException) return true;

        if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException)
            return true;

        return false;
    }
}
