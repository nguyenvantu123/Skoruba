// Feature: tenant-client-cache-public-read, Property 19: SDK retry decision + backoff formula
//
// Drives the retry policy across randomised sequences of HTTP responses /
// transient exceptions and asserts:
//   1. The total number of HTTP attempts is bounded by
//      min(m, MaxRetryAttempts + 1) where m is the sequence length.
//   2. Once a non-retriable status appears, the loop terminates
//      immediately on that status.
//   3. The delay between attempts equals baseDelay * 2^(attempt - 1),
//      capped at min(60s, baseDelay * 2^MaxRetryAttempts).
//   4. For statuses ∈ {400, 401, 403, 404, 405, 429} a single response in
//      the sequence terminates the loop immediately (exactly 1 HTTP call).
//
// Validates: Requirements 11.1, 11.2, 11.3

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

using FluentAssertions;

using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

using Skoruba.Duende.IdentityServer.TenantClientCache.Client.Internal;

using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests.Internal;

public sealed class TenantClientCacheClientRetryPolicyProperties
{
    // ===== Step model ===============================================

    /// <summary>One simulated outcome of an HTTP attempt.</summary>
    public abstract record AttemptStep;

    /// <summary>HTTP response with an explicit status code.</summary>
    public sealed record HttpStep(HttpStatusCode Status) : AttemptStep;

    /// <summary>Transient exception observed during the call.</summary>
    public sealed record TransientStep(TransientKind Kind) : AttemptStep;

    public enum TransientKind { HttpRequestException, SocketException, TaskCanceledTimeout }

    public sealed record SequenceSample(
        AttemptStep[] Steps,
        int MaxRetryAttempts,
        TimeSpan BaseDelay);

    // ===== Generators ===============================================

    public static class Arbs
    {
        // The retriable HTTP statuses (R11.1).
        private static readonly HttpStatusCode[] Retriable =
        {
            HttpStatusCode.InternalServerError,
            HttpStatusCode.BadGateway,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout
        };

        // The non-retriable subset we care about (R11.2). 200/304 also
        // qualify; including them ensures the property is exercised on
        // success-shaped responses too.
        private static readonly HttpStatusCode[] NonRetriable =
        {
            HttpStatusCode.OK,
            HttpStatusCode.NotModified,
            HttpStatusCode.BadRequest,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed,
            (HttpStatusCode)429
        };

        // 50% chance the step is a retriable HTTP status, 25% a transient
        // exception (also retriable per IsTransientNetworkException),
        // 25% a non-retriable HTTP status. This keeps sequences mostly
        // retriable so the loop actually exercises the budget.
        private static Gen<AttemptStep> RetriableStep()
            => Gen.OneOf(
                Gen.Elements(Retriable).Select(s => (AttemptStep)new HttpStep(s)),
                Gen.Elements(Enum.GetValues<TransientKind>()).Select(k => (AttemptStep)new TransientStep(k)));

        private static Gen<AttemptStep> NonRetriableStep()
            => Gen.Elements(NonRetriable).Select(s => (AttemptStep)new HttpStep(s));

        private static Gen<AttemptStep> AnyStep()
            => Gen.Frequency(
                (3, RetriableStep()),
                (1, NonRetriableStep()));

        public static Arbitrary<SequenceSample> Sequence()
            => (from len in Gen.Choose(1, 8)
                from steps in AnyStep().ListOf(len)
                from maxRetries in Gen.Choose(0, 5)
                from baseMs in Gen.Choose(10, 500)
                select new SequenceSample(
                    steps.ToArray(),
                    maxRetries,
                    TimeSpan.FromMilliseconds(baseMs)))
                .ToArbitrary();
    }

    // ===== Reference simulator ======================================

    /// <summary>Outcome of running the retry loop against a step sequence.</summary>
    private sealed record SimResult(
        int HttpAttempts,
        TimeSpan[] DelaysBetweenAttempts,
        AttemptStep TerminatingStep);

    /// <summary>
    /// Drive the retry policy as a higher-level loop would: walk the step
    /// sequence one at a time, ask <see cref="TenantClientCacheClientRetryPolicy.ShouldRetry"/>
    /// (or test <see cref="TenantClientCacheClientRetryPolicy.IsTransientNetworkException"/>)
    /// after each, accumulate the
    /// <see cref="TenantClientCacheClientRetryPolicy.NextDelay"/> values,
    /// and stop when retry is no longer authorised OR the budget is
    /// exhausted.
    /// </summary>
    private static SimResult Simulate(SequenceSample sample, TenantClientCacheClientRetryPolicy policy)
    {
        var delays = new List<TimeSpan>();
        var attempt = 0; // 0-based index of the just-completed call

        foreach (var step in sample.Steps)
        {
            attempt++;

            var canRetry = step switch
            {
                HttpStep http => policy.ShouldRetry(http.Status, attempt - 1, sample.MaxRetryAttempts),
                TransientStep tr => attempt - 1 < sample.MaxRetryAttempts
                                    && TenantClientCacheClientRetryPolicy.IsTransientNetworkException(MakeException(tr.Kind)),
                _ => false
            };

            if (!canRetry)
                return new SimResult(attempt, delays.ToArray(), step);

            // Attempt would be retried — record the delay we'd wait
            // before issuing the next call.
            delays.Add(policy.NextDelay(attempt - 1, sample.BaseDelay));
        }

        // The sequence ended without a non-retriable step. The terminating
        // step is the last one we saw; we did not issue any further call.
        return new SimResult(sample.Steps.Length, delays.ToArray(), sample.Steps[^1]);
    }

    private static Exception MakeException(TransientKind kind) => kind switch
    {
        TransientKind.HttpRequestException => new HttpRequestException("transient"),
        TransientKind.SocketException => new SocketException(),
        TransientKind.TaskCanceledTimeout =>
            new TaskCanceledException("timeout", new TimeoutException("inner")),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // ===== Property 19 ==============================================

    /// <summary>
    /// Property 19: SDK retry decision + backoff formula.
    ///
    /// Universal invariants checked on every randomised sequence:
    /// <list type="bullet">
    ///   <item>Total HTTP attempts ≤ <c>min(stepCount, MaxRetryAttempts + 1)</c> (R11.1).</item>
    ///   <item>If the terminating step is a non-retriable HTTP status, attempts equal the index of that status (R11.2).</item>
    ///   <item>Each delay equals <c>min(60s, baseDelay * 2^attempt)</c> for the
    ///       corresponding attempt index (R11.3).</item>
    /// </list>
    /// </summary>
    [Property(MaxTest = 40, Arbitrary = new[] { typeof(Arbs) },
        DisplayName = "P19: SDK retry decision + backoff formula")]
    public void Property19_RetryDecisionAndBackoff(SequenceSample sample)
    {
        // Feature: tenant-client-cache-public-read, Property 19: SDK
        // retry decision + backoff formula.
        var policy = new TenantClientCacheClientRetryPolicy();
        var result = Simulate(sample, policy);

        // (1) Attempt count is bounded by the budget.
        var maxBudget = sample.MaxRetryAttempts + 1; // initial + retries
        var bound = Math.Min(sample.Steps.Length, maxBudget);
        result.HttpAttempts.Should().BeLessThanOrEqualTo(bound,
            "the retry loop must NEVER exceed MaxRetryAttempts + 1 calls (R11.1)");

        // (2) On a non-retriable step the loop terminates immediately.
        if (result.TerminatingStep is HttpStep http && !IsRetriable(http.Status))
        {
            // Find the index of the first non-retriable HTTP status in
            // the sequence. The simulator must have stopped at exactly
            // that position.
            var firstStop = FindFirstStop(sample.Steps, sample.MaxRetryAttempts);
            result.HttpAttempts.Should().Be(firstStop + 1,
                "the loop must stop on the first non-retriable status (R11.2)");
        }

        // (3) Delay formula: each delay corresponds to the attempt that
        // just authorised a retry.
        for (var i = 0; i < result.DelaysBetweenAttempts.Length; i++)
        {
            var expected = ExpectedDelay(i, sample.BaseDelay);
            result.DelaysBetweenAttempts[i].Should().Be(expected,
                "delay between attempt {0} and attempt {1} must be baseDelay * 2^{0}, capped at 60s (R11.3)",
                i, i + 1);
        }
    }

    /// <summary>
    /// Property 19 spot check: a single non-retriable HTTP status in the
    /// sequence terminates the loop after exactly one HTTP call.
    /// </summary>
    [Property(MaxTest = 25, Arbitrary = new[] { typeof(Arbs) },
        DisplayName = "P19 (spot): single non-retriable status → exactly 1 attempt")]
    public void Property19_NonRetriable_FirstStep_ExactlyOneAttempt(SequenceSample sample)
    {
        // Construct a sequence whose first step is non-retriable. Reuse
        // the sampled budget / baseDelay; replace step[0] with a known
        // non-retriable status and inflate the sequence length.
        var nonRetriable = new HttpStatusCode[]
        {
            HttpStatusCode.BadRequest,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound,
            HttpStatusCode.MethodNotAllowed,
            (HttpStatusCode)429
        };

        foreach (var status in nonRetriable)
        {
            var steps = sample.Steps.Length > 0
                ? Prepend(sample.Steps, new HttpStep(status))
                : new AttemptStep[] { new HttpStep(status) };

            var rebuilt = sample with { Steps = steps };
            var policy = new TenantClientCacheClientRetryPolicy();
            var result = Simulate(rebuilt, policy);

            result.HttpAttempts.Should().Be(1,
                "status {0} must terminate the retry loop on the first attempt (R11.2)", status);
            result.DelaysBetweenAttempts.Should().BeEmpty(
                "no delay is computed when the loop terminates on attempt #1");
        }
    }

    // ===== Helpers ==================================================

    private static bool IsRetriable(HttpStatusCode status)
        => status is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static int FindFirstStop(AttemptStep[] steps, int maxRetries)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            switch (steps[i])
            {
                case HttpStep http when !IsRetriable(http.Status):
                    return i;
                case TransientStep tr when !TenantClientCacheClientRetryPolicy
                    .IsTransientNetworkException(MakeException(tr.Kind)):
                    return i;
            }

            if (i >= maxRetries)
                return i;
        }

        return steps.Length - 1;
    }

    private static TimeSpan ExpectedDelay(int attempt, TimeSpan baseDelay)
    {
        var cap = TimeSpan.FromMinutes(1).Ticks;
        if (attempt < 0 || baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        if (attempt >= 62) return TimeSpan.FromTicks(cap);

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

    private static AttemptStep[] Prepend(AttemptStep[] tail, AttemptStep head)
    {
        var result = new AttemptStep[tail.Length + 1];
        result[0] = head;
        Array.Copy(tail, 0, result, 1, tail.Length);
        return result;
    }
}
