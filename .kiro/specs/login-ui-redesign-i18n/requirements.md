# Requirements Document

Login UI Redesign + Multi-language

## Introduction

Tính năng này **làm mới giao diện** trang đăng nhập của host STS (`Skoruba.Duende.IdentityServer.STS.Identity`) và **mở rộng đa ngôn ngữ** cho toàn bộ luồng đăng nhập. Mục tiêu: cập nhật `/Account/Login` (cả tab "Tài khoản" và tab "Số điện thoại") cùng trang `/Account/LoginWithPhone/Verify` theo mockup mới (nền gradient tối, header brand+nav+CTA, logo trung tâm, pill tenant, tab có gạch chân, input có icon prefix, nút CTA gradient tím, footer điều khoản), đồng thời bổ sung English (`en-US`) thành ngôn ngữ thứ hai bên cạnh Vietnamese (`vi-VN`) hiện có, kèm bộ chuyển ngôn ngữ trên trang đăng nhập.

Phạm vi:

- Chỉ thay đổi UI và resource localization trong STS host. Controller actions, model bindings, route names, anti-forgery, external login providers, "Remember me", và toàn bộ luồng phone-OTP step 1 / step 2 / resend (đã hoàn tất ở spec `phone-otp-login`) **không bị thay đổi về hành vi**, chỉ được restyle và bổ sung khoá localization.
- Tab control hiện có (`tab-account` / `tab-phone`) tiếp tục là HTML/CSS/JS thuần, không AJAX, không jQuery, không persist trạng thái tab.
- Bộ chuyển ngôn ngữ dùng cookie `.AspNetCore.Culture` thông qua endpoint `HomeController.SetLanguage` đã có sẵn — không thêm controller mới cho việc này.
- Default culture giữ là `vi-VN` cho người dùng cuối truy cập tenant Việt Nam; `en-US` được thêm vào danh sách `CultureConfiguration:Cultures`. Cấu trúc resx phải cho phép thêm ngôn ngữ tiếp theo bằng cách bổ sung file `.{culture}.resx` mà **không phải đổi code**.

Ngoài phạm vi (sẽ không làm trong feature này):

- Thay đổi `AccountController.Login` POST handler, model `LoginInputModel`, hoặc validation logic.
- Thay đổi luồng xác thực phone-OTP (Twilio, HMAC+Redis, anti-enumeration delay 200–600 ms, feature flag `PhoneOtpLogin:Enabled`, libphonenumber-csharp, `DefaultRegion=VN`).
- Thay đổi cookie scheme, IdentityServer signing keys, token lifetimes, hoặc cấu hình OIDC client.
- Localize các trang quản lý (`Manage/*`), consent (`Consent/*`), grants (`Grants/*`), device flow (`Device/*`), 2FA (`LoginWith2fa`, `LoginWithRecoveryCode`) — nếu các trang này đang thiếu key tiếng Anh hoặc tiếng Việt, không thuộc phạm vi feature này.
- Tự ý đổi `DefaultRequestCulture` hiện tại (`"en"`) thành culture khác. Default vẫn được resolve theo cấu hình `CultureConfiguration:DefaultCulture`.
- Thêm RTL support, bidi text, hoặc localized number formatting tuỳ biến.
- Thay đổi layout chung (`Views/Shared/_Layout.cshtml`) hoặc footer chung — feature này chỉ chèn UI vào trong page-level template của Login và Verify; nếu layout cần điều chỉnh tối thiểu (ví dụ giữ language switcher khi trang đăng nhập override layout), được phép nhưng phải giữ nguyên markup các nhánh khác (Manage, Grants, ...).

## Glossary

- **STS_Host**: Tiến trình `Skoruba.Duende.IdentityServer.STS.Identity` — IdentityServer host chứa view `/Account/Login`, `/Account/LoginWithPhone/Verify`, controllers `AccountController`, `PhoneLoginController`, `HomeController`, và toàn bộ Resources resx.
- **Login_Page**: View Razor `Views/Account/Login.cshtml` reachable tại GET `/Account/Login`. Là trang điểm vào duy nhất của UI đăng nhập (đã được mở rộng thành tab control hai tab bởi spec phone-otp-login).
- **Phone_Verify_Page**: View Razor `Views/Account/LoginWithPhone/Verify.cshtml` reachable tại GET `/Account/LoginWithPhone/Verify`. Trang full-page (không phải panel), step 2 của luồng phone-OTP.
- **Login_Request_Panel**: Partial Razor `Views/Shared/_PhoneRequestPanel.cshtml` được embed vào Login_Page như tab panel "Số điện thoại"; render `<form method="post">` POST tới `/Account/LoginWithPhone/Request`.
- **Account_Tab**: Tab "Tài khoản" của Login_Page — tab control button `id="tab-account"` và panel `id="panel-account"` chứa form username/password (`form id="local-login-form"`). Đây là **DEFAULT active tab** mỗi lần GET Login_Page.
- **Phone_Tab**: Tab "Số điện thoại" của Login_Page — tab control button `id="tab-phone"` và panel `id="panel-phone"` chứa Login_Request_Panel.
- **Login_Tabs_Asset**: Cặp file frontend hiện có `wwwroot/js/login-tabs.js` và `wwwroot/css/login-tabs.css` cung cấp toggle tab + keyboard navigation (Enter/Space/ArrowLeft/ArrowRight). Feature này KHÔNG thay logic JS, chỉ cập nhật CSS hooks.
- **Tenant_Pill**: Phần tử UI mới (`<span>` hoặc `<div>` trong Login_Page) ngay phía trên tab control, hiển thị nhãn "Tenant Hiện Tại" (key `Login.TenantPillLabel`) kèm host hiện tại (`Context.Request.Host.Value`). Chỉ render KHI `ITenantContextAccessor.Current != null`.
- **Login_Header**: Thanh header phía trên cùng của Login_Page (KHÔNG phải `_Layout.cshtml` chung) — chứa brand text bên trái (`RootConfiguration.AdminConfiguration.PageTitle`), bộ liên kết điều hướng giữa (key `Login.NavProducts`, `Login.NavFeatures`, `Login.NavPricing`), và CTA "Đăng nhập" bên phải (anchor đến `#local-login-form` để scroll). Login_Header KHÔNG ảnh hưởng các trang khác đang dùng `_Layout.cshtml`.
- **Login_Footer_Block**: Khối `<footer>` hoặc `<div>` cuối Login_Page hiển thị câu điều khoản (key `Login.TermsNotice`, có placeholder `{0}` `{1}` cho hai liên kết), bên dưới là ba liên kết utility "Điều khoản dịch vụ", "Chính sách bảo mật", "Hỗ trợ" (keys `Login.TermsLink`, `Login.PrivacyLink`, `Login.SupportLink`). URL của các liên kết này phải lấy từ cấu hình `AdminConfiguration` đã có (`AdminConfiguration:TermsOfServiceUri`, `AdminConfiguration:PrivacyPolicyUri`, `AdminConfiguration:SupportUri`); nếu cấu hình không có sẵn key, ưu tiên dùng giá trị mặc định `#`.
- **Language_Switcher**: Phần tử UI dạng `<select>` hoặc `<button>` + dropdown trên Login_Page và Phone_Verify_Page, cho phép người dùng đổi UI culture. POST tới `/Home/SetLanguage` (action đã có sẵn trong `HomeController`) với anti-forgery token, nhận về cookie `.AspNetCore.Culture` và redirect về `returnUrl`.
- **Resx_File**: File resource `.resx` tại `Resources/Views/{Area}/{ViewName}.{culture}.resx` hoặc `Resources/Controllers/{ControllerName}.{culture}.resx`. Pattern naming theo convention hiện có của repo. Culture suffix theo BCP-47, dùng `vi` (neutral) hoặc `vi-VN` (specific) theo hiện trạng từng file.
- **Default_Culture**: Culture được sử dụng khi request không có cookie `.AspNetCore.Culture`, header `Accept-Language` không khớp, và query string `culture` không hiện diện. Giá trị resolve theo `CultureConfiguration:DefaultCulture` từ `appsettings.json`.
- **Supported_Cultures**: Danh sách culture được list trong dropdown Language_Switcher. Tối thiểu chứa `en` và `vi`. Resolve từ `CultureConfiguration:Cultures` từ `appsettings.json`; nếu rỗng, fallback về `CultureConfiguration.AvailableCultures` cộng thêm `vi`.
- **Brand_Title**: Chuỗi `RootConfiguration.AdminConfiguration.PageTitle` từ `appsettings.json` — không localize, hiển thị nguyên trên Login_Header và Login_Page logo block.
- **Login_View_Model**: Class `Skoruba.Duende.IdentityServer.STS.Identity.ViewModels.Account.LoginViewModel` đã có sẵn. KHÔNG được đổi tên field hoặc xoá field hiện có; được phép thêm field mới.
- **Phone_Verify_View_Model**: Class `PhoneVerifyViewModel`. KHÔNG được đổi tên field hoặc xoá field hiện có; được phép thêm field mới.
- **Localization_Key**: Một entry `<data name="...">` trong Resx_File. Key SHALL viết theo pattern `<Area>.<Section>.<Element>` (ví dụ `Login.Title`, `Login.Subtitle`, `Login.Nav.Products`, `LoginWithPhone.PhoneLabel`); key cũ đã hiện diện trong resx vi (`LoginWithPhone.TabAccount`, `LoginWithPhone.TabPhone`, `LoginWithPhone.GenericError`, ...) SHALL được giữ nguyên tên để không phá build hiện có.
- **Localized_Error_Message**: Thông báo lỗi mà controller hiện tại đang đẩy qua `TempData["PhoneOtpError"]` hoặc `ViewData["PhoneOtpVerifyError"]`. Sau feature, message string SHALL được resolve qua `IStringLocalizer` / `IViewLocalizer` thay vì hard-code tiếng Việt trong controller.

## Requirements

### Requirement 1: Visual redesign cho Login_Page

**User Story:** As an end user landing on `/Account/Login`, I want a modern, focused login UI that matches the new SaaS Platform brand mockup, so that the entry point feels trustworthy and the primary action is obvious.

#### Acceptance Criteria

1. THE Login_Page SHALL render Login_Header at the top of the page containing exactly three regions: a brand region on the left displaying Brand_Title as plain text, a navigation region in the center with three anchor links labelled via keys `Login.Nav.Products`, `Login.Nav.Features`, `Login.Nav.Pricing` (URLs resolved from `AdminConfiguration:MarketingProductsUri`, `AdminConfiguration:MarketingFeaturesUri`, `AdminConfiguration:MarketingPricingUri` with fallback `#`), and a primary CTA region on the right rendering an anchor `<a href="#local-login-form">` labelled via key `Login.HeaderCtaLogin`.
2. WHERE the viewport width is below the Tailwind `md` breakpoint (768 px), THE Login_Header SHALL collapse the navigation region into a `<details>` toggle (no JS framework, native HTML disclosure) AND SHALL keep the brand region and primary CTA region visible.
3. THE Login_Page SHALL render a centered logo block above the title, containing an `<img>` referencing `RootConfiguration.AdminConfiguration.HomePageLogoUri` framed inside a square card element with rounded corners.
4. THE Login_Page SHALL render the page title using key `Login.Title` and a subtitle using key `Login.Subtitle` directly below the logo block.
5. WHERE `ITenantContextAccessor.Current` is not null, THE Login_Page SHALL render Tenant_Pill above the tab control containing the static label resolved from key `Login.TenantPillLabel` and the resolved tenant host string from `Context.Request.Host.Value` rendered as plain text inside the same element.
6. WHERE `ITenantContextAccessor.Current` is null, THE Login_Page SHALL NOT render Tenant_Pill AND SHALL NOT render any placeholder text in its position.
7. THE Login_Page SHALL apply a dark gradient background distinct from the global `_Layout.cshtml` body background, scoped via a CSS hook (e.g. body class `login-shell` set in the `@@section styles` block) so that other pages using `_Layout.cshtml` retain the existing background.
8. WHERE the user has selected the system theme `dark`, THE Login_Page SHALL maintain the dark gradient background AND SHALL maintain readable text contrast meeting WCAG 2.1 AA (4.5:1 for body text, 3:1 for large text) for every visible text element on the page.
9. WHERE the user has selected the system theme `light`, THE Login_Page SHALL maintain the dark gradient background unchanged (the login surface is dark by design) AND SHALL maintain the same WCAG 2.1 AA contrast ratios.
10. THE Login_Page SHALL render the existing `_ValidationSummary` partial inside Account_Tab without modifying the partial markup or moving its position relative to `<input asp-for="Username">`.

### Requirement 2: Visual redesign cho Account_Tab và Phone_Tab

**User Story:** As an end user, I want both login methods (account password and phone OTP) on the same page to share a consistent restyled look, so that switching between tabs feels seamless and the form fields look modern.

#### Acceptance Criteria

1. THE Login_Page SHALL render the tab control (existing `role="tablist"` wrapper) with an underline indicator under the active tab AND SHALL apply the active style only to the tab whose `aria-selected="true"`.
2. THE Login_Page SHALL render the username input inside Account_Tab with a leading icon (existing Lucide `user` icon) positioned inside the input's left padding region AND SHALL keep the existing `asp-for="Username"`, `id="Username"`, `tw-validation` attributes unchanged.
3. THE Login_Page SHALL render the password input inside Account_Tab with a leading icon (Lucide `lock`) AND SHALL keep the existing show/hide toggle button (`id="toggle-password-visibility"`) and its JavaScript behavior unchanged.
4. THE Login_Request_Panel SHALL render the phone number input with a leading icon (Lucide `phone`) inside its left padding region AND SHALL keep the existing `name="PhoneNumber"`, `id="phoneOtpPhoneNumber"`, `type="tel"`, `inputmode="tel"`, `autocomplete="tel"` attributes unchanged.
5. THE Login_Page SHALL render the primary submit button inside Account_Tab using the existing button element `id="login-submit-button"` with a violet gradient background AND SHALL keep the existing submit logic, `name="button" value="login"`, and aria-busy behavior unchanged.
6. THE Login_Request_Panel SHALL render the primary submit button using a violet gradient background matching Account_Tab's submit button AND SHALL render the button label resolved via key `LoginWithPhone.RequestSubmit`.
7. THE Login_Request_Panel SHALL preserve every existing form element exactly: anti-forgery token, `<input type="hidden" name="ReturnUrl">`, honeypot input `name="website"` with `tabindex="-1"` and visually-hidden styling.
8. THE Login_Page SHALL render the helper link "Quên mật khẩu?" inside Account_Tab using the existing anchor `asp-action="ForgotPassword" asp-controller="Account"` with localized text via key `Login.Forgot` AND SHALL render the helper link "Đăng ký ngay" using the existing register link logic gated on `RootConfiguration.RegisterConfiguration.Enabled`.
9. THE Login_Page SHALL preserve the existing external providers grid (`Model.VisibleExternalProviders`) inside Account_Tab without changing its iteration logic, action target `asp-action="ExternalLogin"`, or `asp-route-provider` and `asp-route-returnUrl` parameters.
10. THE Login_Tabs_Asset CSS SHALL define active-tab underline styling, gradient button styling, and leading-icon input styling using CSS variables that follow the existing theme token convention (e.g. `--primary`, `--primary-foreground`) so that no inline styles are introduced into the .cshtml templates.

### Requirement 3: Visual redesign cho Phone_Verify_Page

**User Story:** As a user who has received an OTP, I want the verification page to share the same dark gradient style and brand identity as Login_Page, so that the flow feels continuous and not abandoned in a legacy view.

#### Acceptance Criteria

1. THE Phone_Verify_Page SHALL render Login_Header identical to the Login_Page header (brand, nav links, primary CTA).
2. THE Phone_Verify_Page SHALL render the centered logo block, page title (key `LoginWithPhone.Verify.Title`), and subtitle (key `LoginWithPhone.Verify.Subtitle`) using the same markup pattern as Login_Page.
3. WHERE `ITenantContextAccessor.Current` is not null, THE Phone_Verify_Page SHALL render Tenant_Pill identical to Login_Page.
4. THE Phone_Verify_Page SHALL render the OTP input with a leading icon (Lucide `key`) AND SHALL keep `name="Otp"`, `id="phoneOtpCode"`, `inputmode="numeric"`, `autocomplete="one-time-code"`, and the dynamic `maxlength` from `Model.OtpLength` unchanged.
5. THE Phone_Verify_Page SHALL render the verify submit button using the violet gradient style identical to Account_Tab's submit button AND SHALL keep the existing form action `/Account/LoginWithPhone/Verify`, anti-forgery token, and `<input type="hidden" name="ReturnUrl">` unchanged.
6. THE Phone_Verify_Page SHALL render the resend section as a secondary `<button>` inside its existing form action `/Account/LoginWithPhone/Resend` AND SHALL keep the existing cooldown disabling behavior (`disabled` when `Model.ResendCooldownRemainingSeconds > 0`) unchanged.
7. THE Phone_Verify_Page SHALL render the back-link `<a href="/Account/Login?returnUrl=...">` with text resolved via key `LoginWithPhone.BackToLogin` and styled as a secondary text link, preserving the existing `returnUrl` query-string preservation logic unchanged.
8. THE Phone_Verify_Page SHALL render Login_Footer_Block identical to Login_Page.

### Requirement 4: Footer terms block

**User Story:** As a compliance reviewer, I want the login page to surface terms-of-service and privacy-policy disclosures clearly under the form, so that user consent is transparent on every login.

#### Acceptance Criteria

1. THE Login_Page SHALL render Login_Footer_Block immediately below the login card with a single paragraph resolved via key `Login.TermsNotice`, containing exactly two anchor placeholders for the terms-of-service URL and the privacy-policy URL.
2. THE Login_Page SHALL render three utility links below the terms paragraph using keys `Login.TermsLink`, `Login.PrivacyLink`, `Login.SupportLink` with `href` values resolved from `AdminConfiguration:TermsOfServiceUri`, `AdminConfiguration:PrivacyPolicyUri`, `AdminConfiguration:SupportUri` with fallback `#`.
3. THE Login_Page SHALL render the three utility links inline with separator characters between them AND SHALL apply visited/hover states using the existing theme token convention.
4. WHERE `AdminConfiguration:TermsOfServiceUri` is null or whitespace, THE Login_Page SHALL render the terms link with `href="#"` AND SHALL still render the utility link text so that the layout does not collapse.
5. THE Phone_Verify_Page SHALL render Login_Footer_Block identical to Login_Page.

### Requirement 5: Multi-language resource extension

**User Story:** As an English-speaking operator or end user, I want the login flow to render in English when my UI culture resolves to English, so that I can read the entire experience without translating manually.

#### Acceptance Criteria

1. THE STS_Host SHALL include `Resources/Views/Account/Login.en.resx` containing English values for every Localization_Key referenced from `Views/Account/Login.cshtml`, including the redesign keys (`Login.Title`, `Login.Subtitle`, `Login.TenantPillLabel`, `Login.Nav.Products`, `Login.Nav.Features`, `Login.Nav.Pricing`, `Login.HeaderCtaLogin`, `Login.TermsNotice`, `Login.TermsLink`, `Login.PrivacyLink`, `Login.SupportLink`, `Login.Forgot`, `Login.SignUp`, `Login.NoAccount`, `Login.Or`) AND SHALL include English values for the existing keys already present in `Login.en.resx`.
2. THE STS_Host SHALL include `Resources/Views/Account/Login.vi.resx` containing Vietnamese values for the same keys listed in clause 5.1, including the redesign keys.
3. THE STS_Host SHALL include `Resources/Views/Account/LoginWithPhone/Verify.en.resx` containing English values for every key referenced from `Views/Account/LoginWithPhone/Verify.cshtml` (`LoginWithPhone.Verify.Title`, `LoginWithPhone.Verify.Subtitle`, `LoginWithPhone.OtpLabel`, `LoginWithPhone.MaskedPhonePrefix`, `LoginWithPhone.VerifySubmit`, `LoginWithPhone.Resend`, `LoginWithPhone.BackToLogin`, `LoginWithPhone.GenericVerifyError`, `LoginWithPhone.TabPhone`).
4. THE STS_Host SHALL include `Resources/Views/Account/LoginWithPhone/Verify.vi.resx` containing Vietnamese values for the same keys listed in clause 5.3.
5. THE STS_Host SHALL include `Resources/Views/Shared/_PhoneRequestPanel.en.resx` containing English values for the keys `LoginWithPhone.PhoneLabel`, `LoginWithPhone.PhonePlaceholder`, `LoginWithPhone.RequestSubmit`, `LoginWithPhone.GenericError`, `LoginWithPhone.TabsLabel`, `LoginWithPhone.TabAccount`, `LoginWithPhone.TabPhone`.
6. THE STS_Host SHALL include `Resources/Views/Shared/_PhoneRequestPanel.vi.resx` containing Vietnamese values for the same keys listed in clause 5.5.
7. THE STS_Host SHALL include `Resources/Controllers/AccountController.vi.resx` (currently absent) containing Vietnamese values for every key currently referenced from `AccountController` localized error messages.
8. THE STS_Host SHALL include `Resources/Controllers/PhoneLoginController.en.resx` and `Resources/Controllers/PhoneLoginController.vi.resx` containing the exact two strings used as Localized_Error_Message: the generic step-1 rejection (English: "Cannot send OTP. Please try again in a few minutes.", Vietnamese: "Không thể gửi mã OTP. Vui lòng thử lại sau ít phút.") and the generic verify rejection (English: "OTP is incorrect or has expired.", Vietnamese: "Mã OTP không đúng hoặc đã hết hạn.").
9. THE PhoneLoginController SHALL resolve every Localized_Error_Message via injected `IStringLocalizer<PhoneLoginController>` instead of inline string literals AND SHALL preserve the existing TempData/ViewData key names (`PhoneOtpError`, `PhoneOtpVerifyError`) so that views read the same property names as today.
10. WHEN the resolved UI culture for a request is `en` or `en-US`, THE Login_Page, Login_Request_Panel, and Phone_Verify_Page SHALL render every visible string from English Resx_File entries.
11. WHEN the resolved UI culture for a request is `vi` or `vi-VN`, THE Login_Page, Login_Request_Panel, and Phone_Verify_Page SHALL render every visible string from Vietnamese Resx_File entries.
12. IF a Localization_Key is missing from the resolved culture's Resx_File, THEN the existing `IViewLocalizer` fallback behavior (return the key name) SHALL apply AND THE STS_Host SHALL log a Warning at startup once per missing key under logger category `Localization` so that gaps are visible in operations.

### Requirement 6: Language_Switcher trên trang đăng nhập

**User Story:** As an end user on `/Account/Login`, I want a visible control to change the UI language directly on the login screen, so that I can switch to my preferred language before authenticating.

#### Acceptance Criteria

1. THE Login_Page SHALL render Language_Switcher in Login_Header's right region (next to the primary CTA) on screens at or above the Tailwind `md` breakpoint AND SHALL render Language_Switcher inside the collapsed `<details>` panel below the `md` breakpoint.
2. THE Phone_Verify_Page SHALL render Language_Switcher in Login_Header's right region using the same component as Login_Page.
3. THE Language_Switcher SHALL render a `<form method="post" action="/Home/SetLanguage">` enclosing a `<select name="culture">` whose options are populated from `IOptions<RequestLocalizationOptions>.Value.SupportedUICultures` AND SHALL include an anti-forgery token AND SHALL include a hidden `<input name="returnUrl">` populated from the current request path and query string.
4. THE Language_Switcher SHALL pre-select the option matching `Context.Features.Get<IRequestCultureFeature>()?.RequestCulture.UICulture.Name`.
5. WHEN the user selects an option in the `<select>`, THE Language_Switcher SHALL submit the form via the existing `wwwroot/js/language.js` script behavior (no AJAX, full POST + redirect) AND SHALL NOT introduce new JavaScript files.
6. WHEN the form posts to `/Home/SetLanguage`, THE STS_Host SHALL set the `.AspNetCore.Culture` cookie via the existing `HomeController.SetLanguage` action with `expires = DateTimeOffset.UtcNow.AddYears(1)` AND SHALL redirect (HTTP 302) to the `returnUrl` value preserving any existing query-string parameters.
7. THE Language_Switcher SHALL render each option's display text using `CultureInfo.NativeName` for the corresponding culture (e.g. "English" for `en`, "Tiếng Việt" for `vi`) AND SHALL fall back to `CultureInfo.DisplayName` when `NativeName` is empty.
8. WHERE Supported_Cultures contains fewer than two cultures, THE Language_Switcher SHALL NOT render at all so that no empty dropdown is shown to the user.
9. THE Language_Switcher SHALL include a leading globe icon (Lucide `languages` or `globe`) before the `<select>` AND SHALL include an `aria-label` attribute on the form root resolved via key `Layout.Language` (existing key reused from Shared `_Layout.en.resx`/`_Layout.vi.resx`).

### Requirement 7: Cấu hình culture defaults

**User Story:** As an STS operator, I want a single configuration block to declare which cultures are exposed and which one is the default, so that I can add or remove languages without redeploying the binary.

#### Acceptance Criteria

1. THE STS_Host SHALL read `CultureConfiguration:Cultures` (string array) and `CultureConfiguration:DefaultCulture` (string) from `appsettings.json` and environment variables on startup.
2. WHERE `CultureConfiguration:Cultures` is empty or absent, THE STS_Host SHALL default Supported_Cultures to the union of `CultureConfiguration.AvailableCultures` and `["vi"]` so that Vietnamese is exposed by default.
3. WHERE `CultureConfiguration:DefaultCulture` is null or whitespace, THE STS_Host SHALL set Default_Culture to `"vi"` for STS_Host instances AND SHALL NOT modify `CultureConfiguration.DefaultRequestCulture` in code.
4. THE STS_Host SHALL register `RequestLocalizationOptions` with `DefaultRequestCulture` resolved per clause 7.3 AND SHALL register the providers `QueryStringRequestCultureProvider`, `CookieRequestCultureProvider`, `AcceptLanguageHeaderRequestCultureProvider` in this exact order.
5. THE STS_Host SHALL ship a default `appsettings.json` value declaring `CultureConfiguration:Cultures = ["vi", "en"]` and `CultureConfiguration:DefaultCulture = "vi"` so that out-of-the-box installs surface only Vietnamese and English in Language_Switcher.
6. WHERE an operator adds a new culture code to `CultureConfiguration:Cultures` AND a corresponding Resx_File suffixed with that culture exists for every key referenced by Login_Page and Phone_Verify_Page, THE Login_Page and Phone_Verify_Page SHALL render the new culture in Language_Switcher AND SHALL render localized text without any code change.
7. WHEN the STS_Host starts AND a culture listed in `CultureConfiguration:Cultures` cannot be parsed by `CultureInfo.GetCultureInfo`, THE STS_Host SHALL log an Error containing the offending culture string AND SHALL skip that culture from Supported_Cultures.

### Requirement 8: Accessibility preservation

**User Story:** As a user relying on a keyboard or screen reader, I want every redesigned element on the login flow to remain operable and announced correctly, so that the visual refresh does not regress accessibility.

#### Acceptance Criteria

1. THE Login_Page SHALL retain `role="tablist"` on the tab control wrapper, `role="tab"` on each tab button, `role="tabpanel"` on each panel, `aria-selected` on each tab, `aria-controls` on each tab pointing to its panel `id`, and `aria-labelledby` on each panel pointing to its tab `id`.
2. THE Login_Page SHALL retain `tabindex="0"` on the active tab and `tabindex="-1"` on the inactive tab on initial server render AND SHALL retain Login_Tabs_Asset's keyboard handling for ArrowLeft, ArrowRight, Enter, Space.
3. THE Login_Page SHALL associate every visible input with a `<label for>` matching the input's `id`, including the username input, password input, phone number input, and OTP input on Phone_Verify_Page.
4. THE Login_Page SHALL render every interactive element (buttons, links, selects) with a visible focus ring meeting WCAG 2.1 AA non-text contrast (3:1) against the dark gradient background.
5. THE Login_Page SHALL render Tenant_Pill with `aria-label` resolved via key `Login.TenantPillAriaLabel` containing both the static label and the resolved tenant host so that screen readers announce the full context in one read.
6. THE Login_Page SHALL render the leading icons inside inputs with `aria-hidden="true"` on the icon element AND SHALL keep the input itself focusable so that the icon is decorative only.
7. THE Login_Page SHALL render Language_Switcher with the `<select>` element labelled via the existing `<label asp-for="...">` pattern from `SelectLanguage.cshtml` so that it remains discoverable to assistive tech.
8. THE Phone_Verify_Page SHALL render the OTP input with `aria-describedby` pointing to a hidden `<span>` containing the masked phone number AND SHALL render the resend button with `aria-disabled` mirroring the `disabled` attribute when cooldown is active.
9. WHERE the user navigates the page using only the keyboard, THE Login_Page SHALL allow the focus order to traverse Login_Header → Tenant_Pill → tab buttons → active panel form fields → primary CTA → helper links → Language_Switcher → Login_Footer_Block in the visible top-to-bottom order without any focus traps.

### Requirement 9: Backward compatibility với phone-otp-login spec

**User Story:** As the maintainer of the existing phone-OTP integration tests, I want every route, controller action, model binding, and form contract to remain stable, so that the existing test suite passes without modification.

#### Acceptance Criteria

1. THE STS_Host SHALL preserve the routes `/Account/Login` (GET, POST), `/Account/LoginWithPhone/Request` (POST), `/Account/LoginWithPhone/Verify` (GET, POST), `/Account/LoginWithPhone/Resend` (POST), `/Home/SetLanguage` (POST) with their current HTTP methods and bound parameter names.
2. THE STS_Host SHALL preserve `LoginViewModel` field names `Username`, `Password`, `RememberLogin`, `ReturnUrl`, `EnableLocalLogin`, `AllowRememberLogin`, `HasTenantContext`, `TenantKey`, `LoginResolutionPolicy`, `VisibleExternalProviders` AND `PhoneVerifyViewModel` field names `Otp`, `ReturnUrl`, `MaskedPhone`, `OtpLength`, `ResendCooldownRemainingSeconds`.
3. THE STS_Host SHALL preserve form input `name` attributes `Username`, `Password`, `RememberLogin`, `ReturnUrl`, `PhoneNumber`, `Otp`, `website`, `culture`, `button` so that existing test assertions matching POST bodies continue to pass.
4. THE STS_Host SHALL preserve the anti-forgery token emission for every form (`@@Html.AntiForgeryToken()` or equivalent tag-helper-emitted hidden field) AND SHALL preserve the `[ValidateAntiForgeryToken]` attribute on every existing POST handler.
5. THE STS_Host SHALL preserve the existing TempData/ViewData key names `PhoneOtpError`, `PhoneOtpVerifyError`, `PhoneTabPreActive`, `PhoneOtpResendSuccess`, `PhoneOtpReturnUrl` AND SHALL NOT rename these keys.
6. THE STS_Host SHALL preserve the feature flag check `PhoneOtpLogin:Enabled` AND SHALL preserve the rule that when the flag is `false` or absent the Login_Page renders without the tab control and the phone routes return HTTP 404.
7. THE STS_Host SHALL preserve the existing JavaScript identifiers `tab-account`, `tab-phone`, `panel-account`, `panel-phone`, `local-login-form`, `login-submit-button`, `password-toggle-text`, `cultureSelect`, `selectLanguageForm` AND THE existing `wwwroot/js/login-tabs.js` and `wwwroot/js/language.js` SHALL keep their public DOM contracts unchanged.
8. THE STS_Host SHALL preserve the existing static assets `wwwroot/css/login-tabs.css`, `wwwroot/js/login-tabs.js`, `wwwroot/js/login-tenant-status.js`, `wwwroot/js/language.js` AND SHALL only modify their content additively (add new selectors / new DOM listeners) without removing existing exports or selectors that other pages may depend on.
9. WHEN the existing integration tests under `tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests` are executed against an STS_Host built from this feature, THE tests SHALL pass without any test code change.

### Requirement 10: Security preservation

**User Story:** As a security reviewer, I want the redesign and i18n work to preserve every security boundary already established by the phone-OTP feature and the IdentityServer host, so that no behavior shift slips in through CSS or resx changes.

#### Acceptance Criteria

1. THE STS_Host SHALL preserve the anti-enumeration randomized delay [200 ms, 600 ms] applied to all step-1 rejection cases in `PhoneLoginController.Request` AND SHALL preserve the indistinguishable rejection response markup including the same generic error message text resolved via `IStringLocalizer<PhoneLoginController>`.
2. THE STS_Host SHALL preserve the `phone_otp_session` cookie issuance, expiration, and tenant-key validation logic in `PhoneLoginController.Verify` AND SHALL NOT change the cookie name, payload, or signing scheme.
3. THE STS_Host SHALL preserve the rate-limit windows defined by `PhoneOtpLogin` configuration (`ResendCooldownSeconds`, `IpRateLimitMaxRequests`, `IpRateLimitWindowSeconds`, `MaxVerifyAttemptsPerOtp`, `PhoneVerifyLockoutMaxFailures`, `PhoneVerifyLockoutWindowSeconds`) AND SHALL NOT introduce new client-side bypasses.
4. THE STS_Host SHALL preserve the HMAC-SHA256 OTP hashing via `IDataProtectionProvider.CreateProtector("PhoneOtp.HashKey")` AND SHALL NOT log OTP plaintext, OTP_Hash, or full phone numbers in any log entry emitted by Login_Page, Phone_Verify_Page, or Language_Switcher.
5. THE STS_Host SHALL preserve the `ApplicationSignInManager.SignInAsync` call site and `UserLoginSuccessEvent` raise site in the existing controllers AND SHALL NOT add any new sign-in path that bypasses these calls.
6. THE STS_Host SHALL preserve the `ITenantContextAccessor.Current` resolution path AND SHALL NOT accept any tenant identifier from request body, query string, or cookie payload introduced by this feature.
7. THE STS_Host SHALL preserve `Content-Security-Policy` and other security headers configured for the STS_Host AND SHALL NOT introduce inline `<style>` blocks or inline `<script>` blocks that would require new CSP allowances; the only exception is the existing inline `<script>` block in `Login.cshtml` that handles the password show/hide toggle, which SHALL be preserved unchanged.
8. THE STS_Host SHALL preserve the existing `[ValidateAntiForgeryToken]` attributes on every POST handler invoked by Login_Page, Phone_Verify_Page, and Language_Switcher AND SHALL emit anti-forgery tokens on every form rendered by these pages.
9. THE STS_Host SHALL preserve the existing forwarded-headers handling so that `HttpContext.Connection.RemoteIpAddress` remains correct for rate-limit decisions in `PhoneLoginController` AND SHALL NOT mutate `HttpContext.Connection` from view code or middleware introduced by this feature.

### Requirement 11: Architecture boundaries (AGENTS.md)

**User Story:** As a maintainer enforcing the workspace architecture map, I want the redesign to live entirely in the presentation layer and the localization resources, so that no business rule, persistence concern, or DbContext access leaks into the view tree.

#### Acceptance Criteria

1. THE Login_Page, Login_Request_Panel, and Phone_Verify_Page SHALL NOT inject any `DbContext` (e.g. `IdentityServerDataProtectionDbContext`, `AdminIdentityDbContext`, `MasterDbContext`) AND SHALL NOT call any repository or business-logic service directly from `@@inject` directives.
2. THE Login_Page, Login_Request_Panel, and Phone_Verify_Page SHALL only inject `IViewLocalizer`, `IStringLocalizer<T>`, `IOptions<...>`, `IRootConfiguration`, `ITenantContextAccessor`, `SignInManager<UserIdentity>`, `IUrlHelper`, and existing infrastructure singletons already used by `_Layout.cshtml`.
3. THE STS_Host SHALL place new presentation logic exclusively under `src/Skoruba.Duende.IdentityServer.STS.Identity` (Views, ViewModels, Controllers, wwwroot, Resources) AND SHALL NOT modify projects under `BusinessLogic*`, `EntityFramework*`, `Admin.Api`, `Admin.UI*`, or `TenantInfrastructure` for this feature.
4. THE STS_Host SHALL NOT add new fields to `LoginViewModel` or `PhoneVerifyViewModel` that require a new persistence column, migration, or business-logic service AND SHALL only add view-only fields (e.g. `string? CurrentCultureName`) populated by the controller from existing services.
5. THE STS_Host project file (`Skoruba.Duende.IdentityServer.STS.Identity.csproj`) SHALL preserve the existing `<ImplicitUsings>enable</ImplicitUsings>` setting AND SHALL NOT add `<Nullable>enable</Nullable>`.
6. THE STS_Host SHALL preserve existing folder conventions: views under `Views/{Controller}/{Action}.cshtml`, partials under `Views/Shared/_*.cshtml`, resources under `Resources/Views/{Controller}/{ViewName}.{culture}.resx`, controllers under `Controllers/`, view models under `ViewModels/{Area}/`.
7. WHERE a new class is needed (e.g. a localization-helper or a culture-list provider for the language switcher), THE class SHALL live under `src/Skoruba.Duende.IdentityServer.STS.Identity/Helpers/Localization/` and SHALL be a stateless utility that does not access any database or external HTTP service.

### Requirement 12: Build và validation

**User Story:** As a developer integrating this change, I want the redesign to build and pass linting in a single `dotnet build` command, so that CI does not require new tooling.

#### Acceptance Criteria

1. WHEN `dotnet build src/Skoruba.Duende.IdentityServer.STS.Identity` is executed, THE STS_Host project SHALL build with zero errors and zero new warnings introduced by this feature.
2. WHEN every Resx_File listed in Requirement 5 is added or updated, THE STS_Host project SHALL embed each resx as `EmbeddedResource` per the existing csproj convention AND SHALL produce one `*.resources` artifact per resx in `obj/Debug/{TargetFramework}/`.
3. WHEN `dotnet test tests/Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.IntegrationTests` is executed against the modified STS_Host, THE existing test suite SHALL pass without modification.
4. THE feature SHALL NOT introduce a new npm dependency, a new MSBuild task, or a new tooling requirement beyond the existing `tailwindcss` and `lucide-static` already wired into the project.
5. WHEN `npm run build` is executed in `src/Skoruba.Duende.IdentityServer.STS.Identity`, THE Tailwind build pipeline SHALL produce a `wwwroot/css/app.css` containing the new utility classes referenced by the redesign without manual cache invalidation.

### Requirement 13: Browser support and progressive enhancement

**User Story:** As a user on a slightly older browser, I want the login UI to remain usable when modern CSS features are unavailable, so that I can still authenticate.

#### Acceptance Criteria

1. THE Login_Page SHALL function on Chromium ≥ 110, Firefox ≥ 110, Safari ≥ 16, and Edge ≥ 110 AND SHALL render every form input as keyboard-operable and submit-capable on these browsers.
2. WHERE a browser does not support CSS `backdrop-filter` (used by the existing layout's `bg-background/80 backdrop-blur` header), THE Login_Header SHALL fall back to a solid background color preserving WCAG 2.1 AA contrast ratios.
3. WHERE JavaScript is disabled in the user's browser, THE Login_Page SHALL still render Account_Tab as the default visible panel AND THE password show/hide toggle SHALL be hidden (but the password input itself SHALL remain submittable) AND Language_Switcher SHALL still allow form-based POST to `/Home/SetLanguage` when the user manually triggers the form submit.
4. WHERE JavaScript is disabled, THE Phone_Tab and Login_Request_Panel SHALL still render the form inside the panel and SHALL still allow direct POST to `/Account/LoginWithPhone/Request` so that the phone-OTP flow remains usable when only HTML is available.
