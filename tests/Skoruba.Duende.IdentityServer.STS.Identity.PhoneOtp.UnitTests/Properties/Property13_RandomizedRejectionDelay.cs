// Feature: phone-otp-multi-account-select, Property 13: Randomized rejection delay
//
// Validates: Requirements 11.4, 11.5, 18.7
//
// Property statement (Section 10.3 design):
//   For random rejection branch (Gates 1, 3, 4, 5, 6, 7, 8, 9):
//     Wall-clock latency from POST entry to redirect MUST be >= 100 ms (the
//     lower bound of `DelayJitterAsync`'s 100..300 ms range).
//   For success branch:
//     The controller MUST NOT await `Task.Delay` (R11.5) — wall-clock latency
//     SHALL be small (no padding budget consumed).
//
// Why this property is SKIPPED:
//   Asserting wall-clock latency at unit-test scope is inherently flaky:
//   xUnit + FsCheck schedule iterations on the thread-pool, GC pauses can
//   inflate the lower bound by tens of ms, and Stopwatch resolution differs
//   by host. Reliable property requires either:
//     (a) intercepting `Task.Delay` via a custom TaskScheduler / TimeProvider
//         abstraction the controller doesn't yet take a dependency on; or
//     (b) running the assertion in an integration test where 100 ms is small
//         relative to the rest of the request pipeline so noise is bounded.
//
//   We considered Stopwatch-based "best effort" assertions but they fail
//   non-deterministically on shared CI runners (especially in containers).
//   Per task 11 instructions ("Stopwatch measure (lossy). Chọn skip nếu không
//   trivial"), we choose Skip.
//
// How a future implementer can unblock:
//   * Inject a delegate `Func<int, Task>` (or a lightweight `IDelayClock`
//     interface) into `PhoneLoginController` instead of calling the static
//     `Task.Delay`. The delegate can be a no-op `t => Task.CompletedTask` in
//     unit tests while the production wiring keeps the real `Task.Delay`. The
//     property can then count delegate invocations (rejection branch
//     expects >= 1, success branch expects 0) without depending on wall-clock.
//   * OR add an integration test in Task 12 that asserts latency >= 100 ms on
//     each rejection branch with a generous (e.g. 50 ms) tolerance.

using FsCheck.Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property13_RandomizedRejectionDelay
{
    [Property(MaxTest = 1, Skip = "Wall-clock delay assertion is flaky at unit-test scope. Unblock path: inject IDelayClock into PhoneLoginController so tests can count Delay invocations without depending on real time. Covered indirectly by R11.4/R11.5 contract review and Task 12 integration smoke tests.")]
    public void Rejection_Branch_Awaits_Delay_AtLeast_100ms_AND_Success_Branch_Does_Not_Delay()
    {
        // Pending IDelayClock injection — see file header.
    }
}
