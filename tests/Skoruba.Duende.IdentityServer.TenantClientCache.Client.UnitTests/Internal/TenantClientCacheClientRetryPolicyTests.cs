// Feature: tenant-client-cache-public-read, Task 8
//
// Example-based tests for TenantClientCacheClientRetryPolicy. The
// property-based test (Property 19) lives in
// TenantClientCacheClientRetryPolicyProperties.
//
// Validates: Requirements 11.1, 11.2, 11.3, 11.5

#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests.Internal;

public sealed class TenantClientCacheClientRetryPolicyTests
{
    private const int DefaultMaxAttempts = 2;

    private readonly TenantClientCacheClientRetryPolicy _policy = new();

    // ===== ShouldRetry: 5xx subset =================================

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]   // 500
    [InlineData(HttpStatusCode.BadGateway)]            // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]    // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]        // 504
    public void ShouldRetry_500_502_503_504_Returns_True_When_Attempts_Remain(HttpStatusCode status)
    {
        _policy.ShouldRetry(status, attempt: 0, maxAttempts: DefaultMaxAttempts).Should().BeTrue();
        _policy.ShouldRetry(status, attempt: 1, maxAttempts: DefaultMaxAttempts).Should().BeTrue();
    }

    // ===== ShouldRetry: 4xx never retried (R11.2) ==================

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]            // 400
    [InlineData(HttpStatusCode.Unauthorized)]          // 401
    [InlineData(HttpStatusCode.Forbidden)]             // 403
    [InlineData(HttpStatusCode.NotFound)]              // 404
    [InlineData(HttpStatusCode.MethodNotAllowed)]      // 405
    [InlineData((HttpStatusCode)429)]                  // 429 — Too Many Requests
    public void ShouldRetry_400_401_403_404_405_429_Returns_False_Always(HttpStatusCode status)
    {
        _policy.ShouldRetry(status, attempt: 0, maxAttempts: DefaultMaxAttempts).Should().BeFalse();
        _policy.ShouldRetry(status, attempt: 1, maxAttempts: DefaultMaxAttempts).Should().BeFalse();
    }

    // ===== ShouldRetry: 2xx/3xx never retried ======================

    [Theory]
    [InlineData(HttpStatusCode.OK)]                    // 200
    [InlineData(HttpStatusCode.NotModified)]           // 304
    [InlineData(HttpStatusCode.NoContent)]             // 204
    public void ShouldRetry_NonError_Returns_False_Always(HttpStatusCode status)
    {
        _policy.ShouldRetry(status, attempt: 0, maxAttempts: DefaultMaxAttempts).Should().BeFalse();
    }

    // ===== ShouldRetry: budget exhaustion ===========================

    [Fact]
    public void ShouldRetry_Returns_False_When_Attempt_Equals_MaxAttempts()
    {
        // attempt == maxAttempts means we have already used the entire budget.
        _policy.ShouldRetry(HttpStatusCode.InternalServerError, attempt: DefaultMaxAttempts, maxAttempts: DefaultMaxAttempts)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_Returns_False_When_Attempt_Exceeds_MaxAttempts()
    {
        _policy.ShouldRetry(HttpStatusCode.InternalServerError, attempt: 5, maxAttempts: DefaultMaxAttempts)
            .Should().BeFalse();
    }

    [Fact]
    public void ShouldRetry_Returns_False_When_MaxAttempts_Is_Zero()
    {
        // MaxRetryAttempts=0 means no retries at all.
        _policy.ShouldRetry(HttpStatusCode.InternalServerError, attempt: 0, maxAttempts: 0)
            .Should().BeFalse();
    }

    // ===== NextDelay: exponential backoff ===========================

    [Theory]
    [InlineData(0, 200, 200)]   // 200 * 2^0 = 200ms
    [InlineData(1, 200, 400)]   // 200 * 2^1 = 400ms
    [InlineData(2, 200, 800)]   // 200 * 2^2 = 800ms
    [InlineData(3, 200, 1600)]  // 200 * 2^3 = 1600ms
    [InlineData(0, 500, 500)]
    [InlineData(1, 500, 1000)]
    [InlineData(0, 50, 50)]
    public void NextDelay_Formula_BaseDelay_Times_2Pow_Attempt(int attempt, int baseMs, int expectedMs)
    {
        var actual = _policy.NextDelay(attempt, TimeSpan.FromMilliseconds(baseMs));

        actual.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Theory]
    [InlineData(10, 200)]   // 200 * 2^10 = 204_800ms = ~204.8s → cap at 60s
    [InlineData(20, 200)]   // would be ~58 hours → cap at 60s
    [InlineData(60, 200)]   // would be astronomical → cap at 60s
    [InlineData(80, 200)]   // overflow guard branch → cap at 60s
    public void NextDelay_Caps_At_60_Seconds(int attempt, int baseMs)
    {
        var actual = _policy.NextDelay(attempt, TimeSpan.FromMilliseconds(baseMs));

        actual.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void NextDelay_Negative_Attempt_Returns_Zero()
    {
        // Defensive guard against caller misuse — never produce a
        // negative or absurd delay.
        _policy.NextDelay(-1, TimeSpan.FromMilliseconds(200))
            .Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void NextDelay_Zero_Or_Negative_BaseDelay_Returns_Zero()
    {
        _policy.NextDelay(3, TimeSpan.Zero).Should().Be(TimeSpan.Zero);
        _policy.NextDelay(3, TimeSpan.FromMilliseconds(-100)).Should().Be(TimeSpan.Zero);
    }

    // ===== IsTransientNetworkException ==============================

    [Fact]
    public void IsTransientNetworkException_HttpRequestException_True()
    {
        TenantClientCacheClientRetryPolicy
            .IsTransientNetworkException(new HttpRequestException("boom"))
            .Should().BeTrue();
    }

    [Fact]
    public void IsTransientNetworkException_TaskCanceledException_With_TimeoutInner_True()
    {
        // This is the exact shape produced by HttpClient.Timeout firing on .NET 10.
        var ex = new TaskCanceledException("timeout", new TimeoutException("inner"));

        TenantClientCacheClientRetryPolicy.IsTransientNetworkException(ex).Should().BeTrue();
    }

    [Fact]
    public void IsTransientNetworkException_TaskCanceledException_Without_Inner_False()
    {
        // Caller-supplied CancellationToken cancellation surfaces as
        // TaskCanceledException without an inner exception (R11.5
        // boundary). The retry policy MUST classify that as
        // non-transient so the higher-level loop re-throws.
        var ex = new TaskCanceledException("caller cancelled");

        TenantClientCacheClientRetryPolicy.IsTransientNetworkException(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransientNetworkException_TaskCanceledException_With_Non_TimeoutInner_False()
    {
        // Defensive: an unrelated inner exception is NOT a timeout.
        var ex = new TaskCanceledException("ctx", new InvalidOperationException("unrelated"));

        TenantClientCacheClientRetryPolicy.IsTransientNetworkException(ex).Should().BeFalse();
    }

    [Fact]
    public void IsTransientNetworkException_SocketException_True()
    {
        TenantClientCacheClientRetryPolicy
            .IsTransientNetworkException(new SocketException())
            .Should().BeTrue();
    }

    [Fact]
    public void IsTransientNetworkException_Generic_Exception_False()
    {
        TenantClientCacheClientRetryPolicy
            .IsTransientNetworkException(new InvalidOperationException("not transient"))
            .Should().BeFalse();
    }

    [Fact]
    public void IsTransientNetworkException_Null_False()
    {
        TenantClientCacheClientRetryPolicy
            .IsTransientNetworkException(null!)
            .Should().BeFalse();
    }
}
