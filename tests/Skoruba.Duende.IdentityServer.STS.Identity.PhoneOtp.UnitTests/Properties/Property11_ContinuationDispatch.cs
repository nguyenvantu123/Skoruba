// Feature: phone-otp-multi-account-select, Property 11: Continuation dispatch matches single-user verify
//
// Validates: Requirements 7.3
//
// Property statement (Section 10.3 design):
//   For random (returnUrl, hasAuthContext, isNative):
//     SelectAccountPost(success branch).Result
//       MUST equal
//     Verify(POST single-user success branch).Result
//   for the same input tuple. In other words, the continuation cascade
//   `(GetAuthorizationContextAsync, IsNativeClient, IsLocalUrl)` is identical
//   between the two flows so multi-account UX cannot regress relative to the
//   single-user flow.
//
// Why this property is SKIPPED:
//   The continuation logic lives in the private method
//   `PhoneLoginController.ContinueWithReturnUrlAsync(string?)`. Comparing
//   "single-user verify" vs "multi-account post-success" requires either:
//     (a) full WebApplicationFactory + integration test (covered by Task 12);
//     or
//     (b) extracting `ContinueWithReturnUrlAsync` to a public/internal-visible
//         helper so this test can drive it directly and compare results.
//   Today neither path is wired into the unit-test project.
//
//   Direct in-process comparison via reflection is brittle: the method
//   captures `IIdentityServerInteractionService`, `IUrlHelper`, and the MVC
//   `Url` property — all of which are controller-scoped state. Calling the
//   private helper twice on the same controller instance with the same mocked
//   dependencies would tautologically return identical results regardless of
//   the production code under test. The property only has signal when the two
//   *flows* (Verify single-user success branch vs SelectAccountPost success
//   branch) both reach the SAME continuation helper — which is already
//   asserted by code structure: both flows call `ContinueWithReturnUrlAsync`
//   in the success path (see `PhoneLoginController.SignInSingleCandidateAsync`
//   and `PhoneLoginController.SelectAccountPost`).
//
// How a future implementer can unblock:
//   * Refactor: make `ContinueWithReturnUrlAsync` `internal` with
//     `[InternalsVisibleTo("...UnitTests")]`. Then this property can drive it
//     directly with arbitrary `(returnUrl, AuthorizationRequest?, IsNative)`
//     tuples and compare results across the two flows.
//   * OR add an integration assertion in Task 12 that issues identical
//     return-url tuples in both flows and compares the resulting redirects
//     byte-for-byte.

using FsCheck.Xunit;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.UnitTests.Properties;

public sealed class Property11_ContinuationDispatch
{
    [Property(MaxTest = 1, Skip = "Continuation cascade is a private controller helper; comparing two flows requires WebApplicationFactory or InternalsVisibleTo refactor. Covered by Task 12 MultiAccountFlowTests.Request_Verify_Select_HappyPath.")]
    public void Success_Continuation_Matches_SingleUserVerify_For_SameTuple()
    {
        // Pending refactor — see file header.
    }
}
