# Implementation Plan: Login UI Redesign + Multi-language

## Overview

This plan turns the design document into a sequence of incremental coding tasks scoped exclusively to the STS host project (`src/Skoruba.Duende.IdentityServer.STS.Identity`) per AGENTS.md. Each step is additive: existing routes, controller actions, view-model fields, form contracts, anti-forgery, anti-enumeration delays, cookie schemes, JS files, and the shared `_Layout.cshtml` chrome remain unchanged.

Work order:

1. Foundation (test project, additive config fields, view DTO, `appsettings.json` defaults).
2. New `Helpers/Localization/` utilities + their property tests.
3. Resource (`.resx`) file additions and extensions.
4. Startup wiring + controller localization injection.
5. New `Views/Shared/Common/` chrome partials + Razor-render property tests.
6. Restyle `Login`, `Verify`, `_PhoneRequestPanel` views + Razor-render property tests.
7. Additive CSS rules in `wwwroot/css/login-tabs.css`.
8. Extend `PhoneOtp.IntegrationTests` with new test classes (do not modify existing tests).

Implementation language is C# / Razor / CSS (resx for resources). No new tooling, npm dependency, or MSBuild task is added to the production STS host project. Property tests use `FsCheck.Xunit` and `AngleSharp` in the new unit-test project only.

## Tasks

- [x] 1. Set up STS host unit-test project for property-based and Razor-render tests
  - [x] 1.1 Create `tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests.csproj`
    - Reference `src/Skoruba.Duende.IdentityServer.STS.Identity/Skoruba.Duende.IdentityServer.STS.Identity.csproj` via `<ProjectReference>`
    - Add NuGet packages: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `FsCheck.Xunit`, `AngleSharp`, `Microsoft.AspNetCore.Mvc.Testing`
    - Add the project to `Skoruba.Duende.IdentityServer.Admin.sln` via `dotnet sln add`
    - Verify with `dotnet build tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests`
    - _Requirements: 11.3, 12.1, 12.4_

- [x] 2. Add additive configuration fields, view DTO, and culture defaults
  - [x] 2.1 Add nullable URL properties to `Configuration/AdminConfiguration.cs`
    - Add `string? TermsOfServiceUri`, `string? PrivacyPolicyUri`, `string? SupportUri`, `string? MarketingProductsUri`, `string? MarketingFeaturesUri`, `string? MarketingPricingUri`
    - Do not rename, remove, or change existing properties; preserve `ImplicitUsings` and the absence of `Nullable` at the project level (use C# nullable annotation per-property)
    - _Requirements: 1.1, 4.1, 4.2, 4.4, 11.4, 11.5_

  - [x] 2.2 Create `Models/Login/LoginShellHeaderModel.cs`
    - Plain DTO with `string CurrentPath` and `string CurrentQuery`
    - View-only — no persistence, no business-logic dependency
    - _Requirements: 11.4, 11.6_

  - [x] 2.3 Update `appsettings.json` with `CultureConfiguration` defaults
    - Set `CultureConfiguration:Cultures = ["vi", "en"]` and `CultureConfiguration:DefaultCulture = "vi"`
    - Do not modify any other section
    - _Requirements: 7.1, 7.5_

- [x] 3. Implement Helpers/Localization utilities and their property tests
  - [x] 3.1 Create `Helpers/Localization/CultureConfigurationResolver.cs`
    - Pure static `Resolve(CultureConfiguration?, string fallbackCulture = "vi", IEnumerable<string>? availableCultures = null)` returning `CultureConfigurationResolverResult { SupportedCultures, DefaultCulture, InvalidCultureCodes }`
    - No I/O, no logging, no DI; never throws on bad input — invalid codes returned via `InvalidCultureCodes`
    - Falls back to `CultureConfiguration.AvailableCultures ∪ {fallbackCulture}` when input `Cultures` is null or empty
    - Picks `DefaultCulture` from input when supported, else `fallbackCulture` when supported, else first supported culture
    - Do not mutate the static `CultureConfiguration.DefaultRequestCulture`
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.7, 11.7_

  - [x] 3.2 Create `Helpers/Localization/LocalizationManifestValidator.cs`
    - Define `record LocalizationManifestEntry(Type ResourceType, string Key)`
    - Define static `LocalizationManifest.Entries` listing every key from §7 of the design (Login redesign keys, Verify keys, _PhoneRequestPanel keys, AccountController keys, PhoneLoginController error keys)
    - Define static `LocalizationManifestValidator.ValidateAtStartup(IServiceProvider, IEnumerable<CultureInfo>, ILogger)` that iterates `Entries × supportedUICultures`, resolves `IStringLocalizerFactory.Create(ResourceType)` per entry, and emits one `LogWarning` under category `"Localization"` per missing tuple
    - Use a static `HashSet<(Type, string, string)>` to deduplicate across invocations
    - Read-only, fire-and-forget, never throws, never blocks startup
    - _Requirements: 5.12, 11.7_

  - [x]* 3.3 Property test for resolver — preserves valid input cultures
    - File: `tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests/Helpers/Localization/CultureConfigurationResolver_Property1_Tests.cs`
    - **Property 1: Culture configuration resolver preserves valid input cultures**
    - **Validates: Requirements 7.2, 7.6**

  - [x]* 3.4 Property test for resolver — default culture fallback
    - File: `CultureConfigurationResolver_Property2_Tests.cs`
    - **Property 2: Culture configuration resolver default culture fallback**
    - **Validates: Requirements 7.3**

  - [x]* 3.5 Property test for resolver — isolates invalid culture codes
    - File: `CultureConfigurationResolver_Property3_Tests.cs`
    - **Property 3: Culture configuration resolver isolates invalid culture codes**
    - **Validates: Requirements 7.7**

  - [x]* 3.6 Property test for validator — emits one Warning per missing tuple, deduplicates across invocations
    - File: `LocalizationManifestValidator_Property16_Tests.cs`
    - Use an `IStringLocalizerFactory` stub returning `LocalizedString` with `IsResourceNotFound = true`
    - Capture `ILogger` warnings via a fake logger
    - **Property 16: Localization manifest validator emits exactly one Warning per missing tuple and dedupes across invocations**
    - **Validates: Requirements 5.12**

- [x] 4. Add resource (`.resx`) files
  - [x] 4.1 Extend `Resources/Views/Account/Login.en.resx` and `.vi.resx` with redesign keys
    - Keys: `Login.Title`, `Login.Subtitle`, `Login.TenantPillLabel`, `Login.TenantPillAriaLabel`, `Login.Nav.Products`, `Login.Nav.Features`, `Login.Nav.Pricing`, `Login.HeaderCtaLogin`, `Login.TermsNotice` (with `{0}` `{1}` placeholders), `Login.TermsLink`, `Login.PrivacyLink`, `Login.SupportLink`, `Login.Forgot`, `Login.SignUp`, `Login.NoAccount`, `Login.Or`
    - Preserve every existing key in both files verbatim
    - _Requirements: 5.1, 5.2_

  - [x] 4.2 Create `Resources/Views/Account/LoginWithPhone/Verify.en.resx` and `.vi.resx`
    - Keys: `LoginWithPhone.Verify.Title`, `LoginWithPhone.Verify.Subtitle`, `LoginWithPhone.OtpLabel`, `LoginWithPhone.MaskedPhonePrefix`, `LoginWithPhone.VerifySubmit`, `LoginWithPhone.Resend`, `LoginWithPhone.BackToLogin`, `LoginWithPhone.GenericVerifyError`, `LoginWithPhone.TabPhone`
    - _Requirements: 5.3, 5.4_

  - [x] 4.3 Extend `Resources/Views/Shared/_PhoneRequestPanel.vi.resx` and create `.en.resx`
    - Add missing keys: `LoginWithPhone.PhonePlaceholder`, `LoginWithPhone.TabsLabel`, `LoginWithPhone.TabAccount`, `LoginWithPhone.TabPhone` (vi already has `LoginWithPhone.PhoneLabel`, `LoginWithPhone.RequestSubmit`, `LoginWithPhone.GenericError`)
    - English file mirrors the full key set
    - _Requirements: 5.5, 5.6_

  - [x] 4.4 Create `Resources/Controllers/AccountController.vi.resx`
    - Mirror every key currently referenced from `AccountController` in other languages with Vietnamese values
    - _Requirements: 5.7_

  - [x] 4.5 Create `Resources/Controllers/PhoneLoginController.en.resx` and `.vi.resx`
    - `Generic_Request_Error` — en: "Cannot send OTP. Please try again in a few minutes.", vi: "Không thể gửi mã OTP. Vui lòng thử lại sau ít phút."
    - `Generic_Verify_Error` — en: "OTP is incorrect or has expired.", vi: "Mã OTP không đúng hoặc đã hết hạn."
    - _Requirements: 5.8, 5.9_

- [x] 5. Wire startup and controller to use resolver, validator, and localizer
  - [x] 5.1 Update `Helpers/StartupHelpers.cs::AddMvcWithLocalization`
    - Replace the inline supported-culture / default-culture block with a single call to `CultureConfigurationResolver.Resolve(cultureConfiguration)`
    - Apply `resolved.SupportedCultures` to `opts.SupportedCultures` and `opts.SupportedUICultures`
    - Apply `resolved.DefaultCulture` to `opts.DefaultRequestCulture`
    - Set `opts.RequestCultureProviders = [QueryStringRequestCultureProvider, CookieRequestCultureProvider, AcceptLanguageHeaderRequestCultureProvider]` in this exact order
    - Iterate `resolved.InvalidCultureCodes` and emit `ILogger.LogError` per offending code (use the `ILoggerFactory` available in the configure callback or queue via `IHostApplicationLifetime.ApplicationStarted`)
    - Do not change anything else in this method or other methods of `StartupHelpers.cs`
    - _Requirements: 7.1, 7.4, 7.5, 7.7_

  - [x] 5.2 Run `LocalizationManifestValidator` from `Startup.Configure`
    - After `app.UseRequestLocalization(...)`, resolve `IServiceProvider`, `IOptions<RequestLocalizationOptions>`, and `ILoggerFactory.CreateLogger("Localization")`, then call `LocalizationManifestValidator.ValidateAtStartup(...)`
    - The call MUST be synchronous, non-blocking on failure, and not impact request handling
    - _Requirements: 5.12_

  - [x] 5.3 Inject `IStringLocalizer<PhoneLoginController>` into `Controllers/PhoneLoginController.cs`
    - Add a `private readonly IStringLocalizer<PhoneLoginController> _localizer` field; add it as the last constructor parameter
    - Replace the constants `GenericRequestErrorKey` / `GenericVerifyErrorKey` usage at the call sites: `TempData["PhoneOtpError"] = _localizer["Generic_Request_Error"].Value;` and `ViewData["PhoneOtpVerifyError"] = _localizer["Generic_Verify_Error"].Value;`
    - Preserve verbatim: `RandomNumberGenerator.GetInt32(200, 601)` anti-enumeration delay, cookie issuance/validation, rate-limit windows, `PhoneOtpFeatureGate`, `ApplicationSignInManager.SignInAsync`, `UserLoginSuccessEvent` raise, `ITenantContextAccessor.Current` read, anti-forgery attributes, and TempData/ViewData key names (`PhoneOtpError`, `PhoneOtpVerifyError`, `PhoneTabPreActive`, `PhoneOtpResendSuccess`, `PhoneOtpReturnUrl`)
    - _Requirements: 5.8, 5.9, 9.1, 9.5, 10.1, 10.2, 10.3, 10.4, 10.5, 10.8_

- [x] 6. Checkpoint — run `dotnet build src/Skoruba.Duende.IdentityServer.STS.Identity` and `dotnet test tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests`. Ensure all tests pass, ask the user if questions arise.

- [x] 7. Add login-shell shared chrome partials under `Views/Shared/Common/`
  - [x] 7.1 Create `Views/Shared/Common/_LoginHeader.cshtml`
    - Inputs (model): `LoginShellHeaderModel`. Inject only `IRootConfiguration` and `IViewLocalizer`
    - Three regions: brand (left, `RootConfiguration.AdminConfiguration.PageTitle` plain text), nav (center — `Login.Nav.Products`, `Login.Nav.Features`, `Login.Nav.Pricing` anchors with URLs from `AdminConfiguration:MarketingProductsUri/Features/Pricing` defaulting to `#`), CTA (right — anchor `<a href="#local-login-form">` labelled `Login.HeaderCtaLogin` plus a slot rendering `_LoginLanguageSwitcher`)
    - Below `md` breakpoint, collapse the nav region into a native `<details>`/`<summary>` (no JS framework) and move the language switcher inside the disclosure
    - No DbContext, repository, or BusinessLogic injection
    - _Requirements: 1.1, 1.2, 6.1, 11.1, 11.2_

  - [x] 7.2 Create `Views/Shared/Common/_LoginTenantPill.cshtml`
    - Inject only `ITenantContextAccessor` and `IViewLocalizer`
    - Short-circuit (render nothing) when `TenantContextAccessor.Current == null`
    - Render the static label from `Login.TenantPillLabel` plus `Context.Request.Host.Value` plain text
    - Add `aria-label` resolved from `Login.TenantPillAriaLabel` containing both the label and the host string separated by whitespace
    - _Requirements: 1.5, 1.6, 3.3, 8.5, 11.1, 11.2_

  - [x] 7.3 Create `Views/Shared/Common/_LoginLanguageSwitcher.cshtml`
    - Inject only `IViewLocalizer` and `IOptions<RequestLocalizationOptions>`
    - Render `<form id="selectLanguageForm" method="post" action="/Home/SetLanguage">` with `@Html.AntiForgeryToken()`, `<input type="hidden" name="returnUrl" value="@(currentPath + currentQuery)">`, `<select id="cultureSelect" name="culture">` populated from `RequestLocalizationOptions.SupportedUICultures`
    - Pre-select option matching `Context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.Name`
    - Each option's display text uses `CultureInfo.NativeName` (whitespace fallback to `DisplayName`)
    - Render leading Lucide `languages` (or `globe`) icon with `aria-hidden="true"`
    - `aria-label` on the form root resolved from existing `Layout.Language` key
    - When `SupportedUICultures.Count < 2`, render empty output
    - Do not introduce a new JS file; rely on existing `wwwroot/js/language.js`
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.7, 6.8, 6.9, 9.7, 9.8, 11.1, 11.2_

  - [x] 7.4 Create `Views/Shared/Common/_LoginFooter.cshtml`
    - Inject only `IRootConfiguration` and `IViewLocalizer`
    - One paragraph rendering `Login.TermsNotice` formatted via `string.Format` with two `<a>` placeholders for `AdminConfiguration:TermsOfServiceUri` and `AdminConfiguration:PrivacyPolicyUri`
    - Three utility links: `Login.TermsLink`, `Login.PrivacyLink`, `Login.SupportLink` with `href` from corresponding admin URLs (default `#` when null/whitespace), inline with separator characters
    - All five anchors render even when their URL is null/whitespace (href becomes `#`)
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 11.1, 11.2_

  - [x]* 7.5 Property test 7 — Footer anchors fall back to `#`
    - File: `tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests/Views/Common/LoginFooter_Property7_Tests.cs`
    - Use Razor render harness + AngleSharp to parse rendered HTML
    - **Property 7: Footer anchors fall back to `#` when URLs are null or whitespace**
    - **Validates: Requirements 4.1, 4.2, 4.4**

  - [x]* 7.6 Property test 8 — Tenant pill aria-label contains label and host
    - File: `LoginTenantPill_Property8_Tests.cs`
    - **Property 8: Tenant pill aria-label contains both label and host**
    - **Validates: Requirements 1.5, 8.5**

  - [x]* 7.7 Property test 12 — Language switcher renders form, hidden inputs, select with one selected option per culture
    - File: `LoginLanguageSwitcher_Property12_Tests.cs`
    - **Property 12: Language switcher renders one option per supported culture with the current culture pre-selected**
    - **Validates: Requirements 6.3, 6.4**

  - [x]* 7.8 Property test 13 — Option text falls back from `NativeName` to `DisplayName`
    - File: `LoginLanguageSwitcher_Property13_Tests.cs`
    - **Property 13: Language switcher option text falls back from NativeName to DisplayName**
    - **Validates: Requirements 6.7**

  - [x]* 7.9 Property test 14 — Switcher renders empty when fewer than two cultures
    - File: `LoginLanguageSwitcher_Property14_Tests.cs`
    - **Property 14: Language switcher hides itself when fewer than two cultures are configured**
    - **Validates: Requirements 6.8**

- [x] 8. Restyle login views with the new chrome
  - [x] 8.1 Restyle `Views/Account/Login.cshtml`
    - Wrap existing markup in `<div class="login-shell login-shell--gradient">` and `<main class="login-shell__main">` so the dark gradient is scoped to login pages only
    - Render `_LoginHeader`, conditional `_LoginTenantPill` (only when `ITenantContextAccessor.Current != null`), and `_LoginFooter` partials
    - Keep the existing `local-login-form`, anti-forgery emission, hidden inputs (`Username`, `Password`, `RememberLogin`, `ReturnUrl`, `button=login`, `website`, `PhoneNumber`), `tw-validation` attributes, `_ValidationSummary` partial, password show/hide toggle (`#toggle-password-visibility`), inline `<script>` block, external providers iteration (`Account/ExternalLogin`), and all DOM ids verbatim (`tab-account`, `tab-phone`, `panel-account`, `panel-phone`, `local-login-form`, `login-submit-button`, `password-toggle-text`)
    - Add a leading Lucide `user` / `lock` icon to username / password inputs via the `input-with-icon` wrapper (from CSS task 9.1) plus Tailwind `pl-10`; do not introduce inline styles
    - Apply `.btn-gradient-primary` class on the submit button without changing its existing attributes
    - _Requirements: 1.3, 1.4, 1.7, 1.8, 1.9, 1.10, 2.1, 2.2, 2.3, 2.5, 2.8, 2.9, 8.1, 8.2, 8.3, 8.4, 8.6, 8.9, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7, 10.7_

  - [x] 8.2 Restyle `Views/Account/LoginWithPhone/Verify.cshtml`
    - Wrap with `login-shell` chrome and render `_LoginHeader`, conditional `_LoginTenantPill`, `_LoginFooter`
    - Preserve `<form action="/Account/LoginWithPhone/Verify">`, `<form action="/Account/LoginWithPhone/Resend">`, `@Html.AntiForgeryToken()`, `name="Otp"`, `id="phoneOtpCode"`, `inputmode="numeric"`, `autocomplete="one-time-code"`, dynamic `maxlength="@Model.OtpLength"`, hidden `ReturnUrl`, cooldown disabling logic (`disabled` when `Model.ResendCooldownRemainingSeconds > 0`), `MaskedPhone` rendering, `aria-describedby` on OTP input
    - Render the back-link `<a>` with `href = "/Account/Login"` when `returnUrl` is null/empty, else `"/Account/Login?returnUrl=" + Uri.EscapeDataString(returnUrl)`
    - Render the resend `<button>` with `aria-disabled` mirroring the `disabled` attribute
    - Read the localized verify-error string from `ViewData["PhoneOtpVerifyError"]` (set by controller via `IStringLocalizer`) — do not call `@Localizer["LoginWithPhone.GenericVerifyError"]` from the view
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 8.8, 9.1, 9.3, 9.4, 9.5, 10.1, 10.2_

  - [x] 8.3 Restyle `Views/Shared/_PhoneRequestPanel.cshtml`
    - Preserve every existing element verbatim: `@Html.AntiForgeryToken()`, hidden `ReturnUrl`, honeypot `<input name="website" tabindex="-1">` with visually-hidden styling, phone input `name="PhoneNumber"`, `id="phoneOtpPhoneNumber"`, `type="tel"`, `inputmode="tel"`, `autocomplete="tel"`
    - Add Lucide `phone` icon via `input-with-icon` wrapper + Tailwind `pl-10`; apply `.btn-gradient-primary` on the submit button
    - Read the localized request-error string from `ViewData["PhoneOtpError"]` (set by controller via `IStringLocalizer`)
    - _Requirements: 2.4, 2.6, 2.7, 9.3, 9.4, 13.4_

  - [x]* 8.4 Property test 4 — External providers grid iterates one anchor per visible provider
    - File: `Login_Property4_ExternalProviders_Tests.cs`
    - **Property 4: External providers grid iterates exactly one anchor per visible provider**
    - **Validates: Requirements 2.9, 9.3**

  - [x]* 8.5 Property test 5 — Resend button cooldown binds `disabled` and `aria-disabled`
    - File: `Verify_Property5_ResendCooldown_Tests.cs`
    - **Property 5: Resend button cooldown binds both `disabled` and `aria-disabled`**
    - **Validates: Requirements 3.6, 8.8**

  - [x]* 8.6 Property test 6 — Verify back-link preserves `returnUrl` with URL encoding
    - File: `Verify_Property6_BackLinkReturnUrl_Tests.cs`
    - **Property 6: Verify back-link preserves `returnUrl` with URL encoding**
    - **Validates: Requirements 3.7, 9.1**

  - [x]* 8.7 Property test 9 — Every visible input has an associated label
    - File: `LoginViews_Property9_InputLabels_Tests.cs`
    - **Property 9: Every visible input has an associated label**
    - **Validates: Requirements 8.3**

  - [x]* 8.8 Property test 10 — Form `name` attributes preserved per page
    - File: `LoginViews_Property10_FormNames_Tests.cs`
    - **Property 10: Form `name` attributes preserved per page**
    - **Validates: Requirements 9.3**

  - [x]* 8.9 Property test 11 — Anti-forgery token count equals form count
    - File: `LoginViews_Property11_AntiForgeryParity_Tests.cs`
    - **Property 11: Anti-forgery token count equals form count**
    - **Validates: Requirements 9.4, 10.8**

- [x] 9. Append additive CSS rules to `wwwroot/css/login-tabs.css`
  - [x] 9.1 Add new selectors using existing CSS variables (`--primary`, `--primary-foreground`, `--background`, `--foreground`, `--border`, `--muted-foreground`)
    - `.login-shell`, `.login-shell--gradient`: scoped dark gradient background
    - `.login-shell__main`, `.login-shell__logo-block`, `.login-shell__title`, `.login-shell__subtitle`, `.login-shell__header`, `.login-shell__footer`, `.login-shell__tenant-pill`, `.login-shell__lang-switcher`: layout helpers
    - `.login-card`: rounded card surface
    - `.input-with-icon`: relative wrapper that absolute-positions the leading icon
    - `.btn-gradient-primary`: violet gradient submit button
    - `@supports (backdrop-filter: blur(0))` block for header surface; fallback uses solid background meeting WCAG AA contrast
    - Do not modify or remove the existing `[role="tab"][aria-selected="true"]` rule or any other existing selector
    - Verify with `npm run build` in `src/Skoruba.Duende.IdentityServer.STS.Identity` if a Tailwind build is needed
    - _Requirements: 1.7, 1.8, 1.9, 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.10, 8.4, 9.7, 9.8, 10.7, 12.5, 13.2_

- [x] 10. Checkpoint — run `dotnet build`, `dotnet test tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests`, and `npm run build` (in the STS host project) if Tailwind output changed. Ensure all tests pass, ask the user if questions arise.

- [x] 11. Extend `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests` (additive only — do not modify existing tests)
  - [-]* 11.1 Add `LoginRedesignTests` integration class
    - Use the existing `WebApplicationFactory<Program>` pattern
    - Assert: the rendered `/Account/Login` contains the new chrome elements (`login-shell` wrapper, `_LoginHeader`, `_LoginFooter`), preserves all required `name` attributes, anti-forgery tokens, JS DOM ids, and external providers grid under both `vi` and `en` resolved cultures
    - _Requirements: 9.9, 12.3_

  - [-]* 11.2 Add `PhoneVerifyRedesignTests` integration class
    - Assert: `/Account/LoginWithPhone/Verify` renders `_LoginHeader`, `_LoginFooter`, conditional `_LoginTenantPill`, OTP input contracts, resend cooldown attributes, and back-link `returnUrl` round-trip under both cultures
    - _Requirements: 9.9, 12.3_

  - [-]* 11.3 Property test 17 — `SetLanguage` sets long-lived cookie and 302-redirects preserving `returnUrl`
    - File: `LanguageSwitcher_Property17_Tests.cs`
    - Drive via `TestServer` HTTP POST to `/Home/SetLanguage` with valid anti-forgery
    - Assert HTTP 302, `Location` header byte-equal to input `returnUrl`, `.AspNetCore.Culture` `Set-Cookie` `Expires` between `now + 364 days` and `now + 366 days`
    - **Property 17: SetLanguage sets a long-lived cookie and redirects preserving the returnUrl**
    - **Validates: Requirements 6.6, 9.1, 9.4**

  - [x]* 11.4 Property test 15 — Manifest covers required keys via real STS host services
    - File: `LocalizationManifest_Property15_Tests.cs`
    - Resolve the real `IStringLocalizerFactory` from the host services; for every `(Entry, CultureInfo)` in `LocalizationManifest.Entries × resolvedSupportedUICultures`, assert `LocalizedString.IsResourceNotFound == false` and `Value != Key`
    - **Property 15: Localization manifest covers every required key in every supported culture**
    - **Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5, 5.6, 5.7, 5.8, 5.10, 5.11**

- [x] 12. Final checkpoint — run `dotnet build`, `dotnet test tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests`, and `dotnet test tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests`. Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for a faster MVP (tests and integration scaffolding). Core implementation tasks are not optional.
- All work is confined to `src/Skoruba.Duende.IdentityServer.STS.Identity/` and `tests/Skoruba.Duende.IdentityServer.STS.Identity.UnitTests/` + `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests/` per AGENTS.md and Requirements 11.3 / 11.6 / 11.7.
- No DbContext, repository, or BusinessLogic service is injected into any view; partials may inject only `IViewLocalizer`, `IStringLocalizer<T>`, `IOptions<...>`, `IRootConfiguration`, `ITenantContextAccessor`, `IUrlHelper`.
- Anti-forgery tokens, anti-enumeration delay [200, 600] ms, cookie schemes, rate-limit windows, OTP HMAC scheme, `ApplicationSignInManager.SignInAsync`, and `UserLoginSuccessEvent` raise are preserved verbatim per Requirements 9 and 10.
- `wwwroot/js/login-tabs.js`, `wwwroot/js/language.js`, `wwwroot/js/login-tenant-status.js`, and the existing inline `<script>` block in `Login.cshtml` (password toggle + aria-busy) are not modified.
- CSS work is additive only: no existing rule is removed or altered.
- New `.resx` files are picked up by the existing `<EmbeddedResource Include="Resources\**\*.resx" />` glob; no `.csproj` change is required.
- `CultureConfiguration.AvailableCultures` and `CultureConfiguration.DefaultRequestCulture` static fields are not mutated; the STS-host fallback (`"vi"`) is applied by `CultureConfigurationResolver`.
- Existing tests in `PhoneOtp.IntegrationTests` are extended (new files added) rather than modified per Requirement 9.9.
- Property tests use `FsCheck.Xunit` configured with `[Property(MaxTest = 100)]`; Razor render assertions use `AngleSharp` for CSS-selector-based DOM checks.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "2.1", "2.2", "2.3", "3.1", "3.2", "4.1", "4.2", "4.3", "4.4", "4.5"] },
    { "id": 1, "tasks": ["3.3", "3.4", "3.5", "3.6", "5.1", "5.2", "5.3", "7.1", "7.2", "7.3", "7.4"] },
    { "id": 2, "tasks": ["7.5", "7.6", "7.7", "7.8", "7.9", "8.1", "8.2", "8.3", "9.1"] },
    { "id": 3, "tasks": ["8.4", "8.5", "8.6", "8.7", "8.8", "8.9", "11.1", "11.2", "11.3", "11.4"] }
  ]
}
```
