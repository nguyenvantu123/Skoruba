# Requirements Document

Phone OTP Login

## Introduction

Tính năng này bổ sung phương thức đăng nhập **không mật khẩu bằng số điện thoại + OTP qua SMS** vào host STS hiện có (`Skoruba.Duende.IdentityServer.STS.Identity`). Mục tiêu: cho phép người dùng đã tồn tại (đã có `PhoneNumber` và `PhoneNumberConfirmed = true` trong tenant tương ứng) đăng nhập bằng cách nhận mã OTP qua SMS, mà không phá vỡ luồng đăng nhập username/password hiện tại, không thay đổi cookie scheme, IdentityServer signing keys, token lifetimes, hoặc cấu hình OIDC dòng client.

UX surface (đã chốt sau làm rõ):

- Trang `/Account/Login` (`Views/Account/Login.cshtml`) hiện tại sẽ được mở rộng thành **tab control** với hai tab: "Tài khoản" (username/password — DEFAULT active) và "Số điện thoại" (phone-OTP step 1).
- Tab control là HTML/CSS/JS thuần, **không AJAX**, **không phụ thuộc thư viện UI mới** (không jQuery, không component framework). Cả hai panel đều render server-side trong cùng response; JavaScript chỉ toggle `.active` / `aria-selected` / `hidden`.
- Form username/password hiện tại được giữ **nguyên hành vi và markup nội bộ** — chỉ được wrap trong panel "Tài khoản"; `asp-action`, `asp-controller`/`asp-route`, model binding, validation summary, anti-forgery, external providers, và "Quên mật khẩu"/"Cancel" không thay đổi.
- Step 1 (yêu cầu OTP) được nhúng dưới dạng form bên trong panel "Số điện thoại" của `/Account/Login`. POST của form này, khi thành công, **redirect HTTP 302** sang một trang riêng `/Account/LoginWithPhone/Verify` (step 2). Step 2 không phải là một panel — nó là một view riêng biệt với URL riêng, để bookmark / refresh / back-button hoạt động đúng.
- Resend OTP nằm trên `/Account/LoginWithPhone/Verify`.
- Trạng thái tab không được persist (không cookie, không localStorage). Mỗi lần load `/Account/Login` đều mặc định active tab "Tài khoản".
- Cả hai luồng đều trả về cùng một `returnUrl` continuation logic mà `AccountController.Login` hiện đang dùng cho IdentityServer authorization context.

Phạm vi:

- Toàn bộ UI và endpoint OTP nằm trong STS host. Các WebApp/SPA client tiếp tục là OIDC client thuần và không xử lý credentials.
- Multi-tenant: số điện thoại unique theo từng tenant (cùng số có thể tồn tại ở 2 tenant khác nhau). Tenant được giải quyết qua `ITenantContextAccessor` đã có (subdomain-based).
- SMS provider: Twilio. Ẩn sau abstraction `ISmsSender` để test không gọi Twilio thật.
- Lưu trữ OTP: chỉ trong distributed cache (Redis) với TTL ngắn, có hash, không lưu plaintext lâu hơn vòng đời request verify.
- Tính năng OFF mặc định, bật qua flag `PhoneOtpLogin:Enabled`.

Ngoài phạm vi (sẽ không làm trong feature này):

- Auto-provision user mới từ số điện thoại.
- Đăng ký, cập nhật, hoặc xác thực số điện thoại trên Admin UI (đã có flow riêng).
- Đăng nhập OTP qua kênh khác SMS (email, voice call, push).
- CAPTCHA bắt buộc (chỉ khuyến nghị triển khai sau khi quan sát abuse) — chỉ expose extension point `IPhoneOtpAntiBotChallenge`.
- Persist last-used tab ở client storage hay cookie.

## Glossary

- **STS_Host**: Tiến trình `Skoruba.Duende.IdentityServer.STS.Identity` — IdentityServer host nơi UI đăng nhập và endpoint OTP cư trú.
- **Account_Controller**: Controller MVC `AccountController<TUser, TKey>` hiện tại trong STS_Host xử lý `/Account/Login` và logout. KHÔNG bị thay đổi bởi feature này; hành vi của form username/password được giữ nguyên 100%.
- **Phone_Login_Controller**: Controller MVC mới được thêm vào STS_Host, xử lý các endpoint phone-OTP. POST step 1 ở `/Account/LoginWithPhone/Request` — đây là action target của form trong tab "Số điện thoại" của Login_Page, KHÔNG có view GET tương ứng và KHÔNG có GET endpoint nào trả HTML cho step 1 ngoài chính `/Account/Login`. GET + POST step 2 ở `/Account/LoginWithPhone/Verify` (render Phone_Verify_Page — view riêng biệt). POST resend ở `/Account/LoginWithPhone/Resend`. Phone_Login_Controller KHÔNG đăng ký bất kỳ liên kết "Đăng nhập bằng số điện thoại" nào như một entry-point độc lập trên Login_Page hay layout chung.
- **Login_Page**: View `/Account/Login` (file `Views/Account/Login.cshtml`). Sau feature này, file vẫn là điểm vào duy nhất cho UI đăng nhập và được mở rộng thành tab control hai tab render server-side: tab "Tài khoản" (DEFAULT active mỗi lần GET) chứa form username/password hiện tại không sửa đổi, và tab "Số điện thoại" chứa Phone_Request_Page. Tab control chỉ render KHI `PhoneOtpLogin:Enabled = true` và có tenant context; KHI flag tắt, Login_Page render y hệt như trước feature (không có tablist, không có tab buttons, không có panel "Số điện thoại"). Trạng thái tab KHÔNG persist (no cookie, no localStorage, no query-string). Login_Page KHÔNG render thêm liên kết hay nút "Đăng nhập bằng số điện thoại" nào ngoài hai tab.
- **Phone_Request_Page**: Tab panel (`role="tabpanel"`) "Số điện thoại" bên trong Login_Page; KHÔNG phải view riêng (file `Views/Account/LoginWithPhone/Request.cshtml` KHÔNG tồn tại) và KHÔNG có URL độc lập. Phone_Request_Page render một `<form method="post">` với input số điện thoại, hidden `returnUrl`, hidden honeypot field tên `website`, và anti-forgery token; `asp-action`/`action` POST tới `/Account/LoginWithPhone/Request`. KHI POST handler thành công, Phone_Login_Controller SHALL redirect HTTP 302 sang `/Account/LoginWithPhone/Verify` (Phone_Verify_Page), KHÔNG bao giờ chuyển sang panel khác trong cùng trang Login_Page.
- **Phone_Verify_Page**: View HTML đầy đủ độc lập `Views/Account/LoginWithPhone/Verify.cshtml` tại URL `/Account/LoginWithPhone/Verify` — KHÔNG phải tab/panel của Login_Page. Hiển thị form nhập OTP, nút Resend (POST `/Account/LoginWithPhone/Resend`), và back-link `<a>` về `/Account/Login` có preserve `returnUrl`. Là trang full-page (không AJAX, không panel) nên bookmark / refresh / back-button hoạt động đúng. KHI `PhoneOtpLogin:Enabled = false`, GET endpoint của Phone_Verify_Page SHALL trả HTTP 404.
- **Login_Tabs_Asset**: Cặp file frontend mới `wwwroot/js/login-tabs.js` và `wwwroot/css/login-tabs.css` (hoặc CSS hooks tương đương trong `wwwroot/css/app.css`) cung cấp toggle tab + keyboard navigation. JS thuần, không jQuery, không AJAX.
- **Phone_Otp_Service**: Service domain mới (`IPhoneOtpService` + implementation) chịu trách nhiệm sinh OTP, hash, lưu vào Otp_Store, kiểm tra OTP, đếm số lần thử, và áp dụng rate-limit.
- **Sms_Sender**: Abstraction `ISmsSender` với method `SendAsync(string e164PhoneNumber, string body, CancellationToken ct)`.
- **Twilio_Sms_Sender**: Implementation của Sms_Sender dùng Twilio .NET SDK chính thức.
- **Fake_Sms_Sender**: Implementation in-memory của Sms_Sender dùng cho unit/integration test, ghi tin nhắn vào collection thay vì gọi Twilio.
- **Otp_Store**: Distributed cache (Redis) lưu trữ OTP đã hash + metadata (tenant_key, phone, attempt_count, expires_at). Dùng key prefix riêng `"otp:"` để tách khỏi `"tenant-registry:"`.
- **Tenant_Context**: Đối tượng `TenantContext` truy cập qua `ITenantContextAccessor.Current`, chứa `TenantKey`.
- **User_Identity**: Entity `UserIdentity` (`Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity`) — thừa kế `IdentityUser`, có sẵn các field `PhoneNumber`, `PhoneNumberConfirmed`, và bổ sung `TenantKey`, `BranchCode`.
- **Application_Sign_In_Manager**: `ApplicationSignInManager<TUser>` hiện có trong STS_Host — dùng để issue cookie sau khi xác thực thành công.
- **Sms_Configuration**: Section cấu hình mới `SmsConfiguration` trong `appsettings.json` (sub-section `Twilio` với `AccountSid`, `AuthToken`, `FromNumber`).
- **Phone_Otp_Configuration**: Section cấu hình mới `PhoneOtpLogin` chứa flag `Enabled`, các tham số TTL, độ dài, rate-limit.
- **OTP**: One-Time Password — mã số dùng một lần, độ dài cố định cấu hình được (mặc định 6 chữ số).
- **OTP_Hash**: HMAC-SHA256(otp_plaintext, server_secret), được lưu vào Otp_Store thay cho plaintext.
- **Generic_Error**: Thông báo lỗi giống nhau cho tất cả các trường hợp từ chối yêu cầu OTP (số chưa đăng ký, số chưa confirm, rate-limit, tenant không xác định, Twilio lỗi). Mục tiêu: không leak thông tin enumeration. Văn bản tiếng Việt: "Không thể gửi mã OTP. Vui lòng thử lại sau ít phút."
- **Generic_Verify_Error**: Thông báo lỗi giống nhau cho mọi trường hợp verify thất bại (sai mã, hết hạn, vượt số lần thử, không có session). Văn bản: "Mã OTP không đúng hoặc đã hết hạn."
- **E164**: Định dạng số điện thoại quốc tế chuẩn ITU-T E.164 (dấu `+` và chữ số, tổng tối đa 15 chữ số).
- **Return_Url**: Tham số `returnUrl` mà Account_Controller hiện tại đang dùng để tiếp tục flow `/connect/authorize` của IdentityServer.

## Requirements

### Requirement 1: Cấu hình bật/tắt tính năng

**User Story:** As an STS operator, I want a single configuration flag to enable or disable phone-OTP login, so that the feature can be rolled out per-environment without redeploying code.

#### Acceptance Criteria

1. THE STS_Host SHALL read configuration value `PhoneOtpLogin:Enabled` (boolean) from `appsettings.json` and environment variables on startup.
2. WHERE `PhoneOtpLogin:Enabled` is `false` or absent, THE Login_Page SHALL omit the tab-control markup entirely (no `role="tablist"` element, no Phone_Request_Page, no Login_Tabs_Asset references, no "Đăng nhập bằng số điện thoại" link or button anywhere on the page or in the shared layout) so that screen readers do not announce an empty tablist AND THE Login_Page SHALL render only the existing username/password form exactly as before this feature.
3. WHERE `PhoneOtpLogin:Enabled` is `false` or absent, THE Phone_Login_Controller routes (`/Account/LoginWithPhone/Request`, `/Account/LoginWithPhone/Verify`, `/Account/LoginWithPhone/Resend`) SHALL return HTTP 404.
4. WHERE `PhoneOtpLogin:Enabled` is `true` AND environment is Production AND any of `SmsConfiguration:Twilio:AccountSid`, `SmsConfiguration:Twilio:AuthToken`, `SmsConfiguration:Twilio:FromNumber` is null or whitespace, THE STS_Host SHALL fail-fast at startup with an exception describing the missing key.
5. WHERE `PhoneOtpLogin:Enabled` is `true` AND environment is non-Production AND Twilio configuration is missing, THE STS_Host SHALL register Fake_Sms_Sender and log a Warning naming each missing key.
6. THE STS_Host SHALL apply the default values `OtpLength=6`, `OtpTtlSeconds=300`, `ResendCooldownSeconds=60`, `MaxVerifyAttemptsPerOtp=5`, `IpRateLimitWindowSeconds=600`, `IpRateLimitMaxRequests=10`, `PhoneVerifyLockoutWindowSeconds=3600`, `PhoneVerifyLockoutMaxFailures=10`, `DefaultRegion="VN"` WHEN the corresponding `PhoneOtpLogin` configuration keys are absent.

### Requirement 2: Tab control trên trang đăng nhập hiện tại

**User Story:** As an end user, I want to switch between username/password and phone-OTP on the same login page without a full page navigation, so that the choice is obvious and I do not lose visual context.

#### Acceptance Criteria

1. WHERE `PhoneOtpLogin:Enabled` is `true` AND `ITenantContextAccessor.Current` is not null, THE Login_Page SHALL render a container element with `role="tablist"` containing exactly two child tab buttons: the first labelled "Tài khoản" (key `LoginWithPhone.TabAccount` via `IViewLocalizer`) and the second labelled "Số điện thoại" (key `LoginWithPhone.TabPhone`).
2. THE Login_Page SHALL render each tab button with `role="tab"`, a unique `id`, an `aria-controls` attribute pointing at the matching panel `id`, an `aria-selected` attribute (`"true"` for the active tab, `"false"` for the inactive tab), and a `tabindex` value of `0` for the active tab and `-1` for the inactive tab.
3. THE Login_Page SHALL render two sibling panel elements with `role="tabpanel"`, each carrying a `tabindex` of `0`, an `aria-labelledby` attribute pointing at the matching tab button `id`, AND THE panel that does not match the active tab SHALL carry the HTML `hidden` attribute on initial server render.
4. THE Login_Page SHALL set the default active tab on every fresh GET to "Tài khoản" AND SHALL NOT read or write any cookie, localStorage, sessionStorage, or query-string parameter to remember a previously-used tab.
5. THE Login_Page SHALL embed the existing username/password form (the entire `<form id="local-login-form">` block including `_ValidationSummary`, external providers, "Quên mật khẩu", and "Cancel") inside the "Tài khoản" panel without modifying the form's `asp-action`, `asp-controller`/`asp-route`, model binding, input names, validation summary partial, or submit button behavior.
6. THE Login_Page SHALL embed the Phone_Request_Page (defined in Requirement 3) inside the "Số điện thoại" panel AND SHALL NOT render any standalone "Đăng nhập bằng số điện thoại" link, button, or anchor outside the tablist.
7. THE Login_Page SHALL include the Login_Tabs_Asset script (`wwwroot/js/login-tabs.js`) on the page only WHERE `PhoneOtpLogin:Enabled` is `true` AND THE script SHALL be the only mechanism that toggles `aria-selected`, `tabindex`, the `hidden` attribute on panels, AND a single CSS class hook (e.g. `is-active`) used for visual state.
8. WHEN a user activates a tab via mouse click or via Enter/Space on a focused tab button, THE Login_Tabs_Asset SHALL set `aria-selected="true"` on the activated tab, set `aria-selected="false"` on the other tab, set `tabindex="0"` on the activated tab, set `tabindex="-1"` on the other tab, remove the `hidden` attribute from the matching panel, add the `hidden` attribute to the other panel, AND move keyboard focus to the activated tab button.
9. WHEN a user presses ArrowLeft or ArrowRight while a tab button has focus, THE Login_Tabs_Asset SHALL move focus and activation to the other tab in a wrap-around fashion AND SHALL apply the same DOM updates listed in clause 2.8.
10. THE Login_Tabs_Asset SHALL NOT make any AJAX request, SHALL NOT load jQuery or any other library, SHALL NOT submit either form, AND SHALL NOT mutate the inputs or values inside the panels.

### Requirement 3: Phone request form trong panel "Số điện thoại"

**User Story:** As an existing user with a confirmed phone number, I want to request a one-time code by entering my phone number directly on the login page, so that I can stay on the same URL until I receive the code.

#### Acceptance Criteria

1. THE Login_Page SHALL render the Phone_Request_Page inside the "Số điện thoại" panel as a `<form method="post">` whose action posts to `/Account/LoginWithPhone/Request`; THE Phone_Request_Page SHALL NOT exist as a standalone view file or have any GET endpoint that returns HTML for step 1.
2. THE Phone_Request_Page SHALL include exactly these fields: a visible `PhoneNumber` input with `type="tel"`, `inputmode="tel"`, and an associated `<label for>`; a hidden `ReturnUrl` input populated from the current request's `returnUrl` query string (or empty when absent); a hidden honeypot input named `website` with `tabindex="-1"`, `autocomplete="off"`, and visually-hidden styling; AND the standard ASP.NET Core anti-forgery token via `@Html.AntiForgeryToken()` (or equivalent tag-helper-emitted hidden field).
3. THE Phone_Request_Page SHALL declare `[ValidateAntiForgeryToken]` on its POST handler in Phone_Login_Controller AND SHALL accept POST only.
4. WHEN a user submits the Phone_Request_Page form with a non-empty `PhoneNumber`, THE Phone_Login_Controller SHALL normalize the input to E164 format using a deterministic, configuration-driven default region (`PhoneOtpLogin:DefaultRegion`, ISO-3166 alpha-2; default `"VN"`).
5. IF the normalized `PhoneNumber` is not a valid E164 number, THEN THE Phone_Login_Controller SHALL re-render Login_Page with the "Số điện thoại" tab pre-activated server-side (via the same tab markup but with `aria-selected`/`hidden` swapped) AND SHALL display Generic_Error inside the Phone_Request_Page's validation area.
6. IF `ITenantContextAccessor.Current` is null at the moment the request is received, THEN THE Phone_Login_Controller SHALL re-render Login_Page with the "Số điện thoại" tab pre-activated AND Generic_Error displayed AND SHALL emit a log entry at level Warning containing `Reason="MissingTenantContext"`.
7. WHEN the Phone_Login_Controller has a valid `TenantKey` and a normalized E164 number, THE Phone_Login_Controller SHALL look up a User_Identity matching `PhoneNumber == normalized AND PhoneNumberConfirmed == true AND TenantKey == current_tenant_key`.
8. IF no User_Identity matches, THEN THE Phone_Login_Controller SHALL respond with the indistinguishable rejection from Requirement 7 after a randomized delay sampled uniformly from the interval [200ms, 600ms] AND SHALL NOT call Sms_Sender.
9. IF a User_Identity matches AND any rate-limit defined in Requirement 6 is exceeded, THEN THE Phone_Login_Controller SHALL respond with the indistinguishable rejection from Requirement 7 after a randomized delay sampled uniformly from the interval [200ms, 600ms] AND SHALL NOT call Sms_Sender.
10. WHEN a User_Identity matches AND no rate-limit is exceeded, THE Phone_Otp_Service SHALL generate a numeric OTP of length `OtpLength` using a cryptographically secure random source.
11. WHEN an OTP is generated, THE Phone_Otp_Service SHALL store, in Otp_Store under key `"otp:" + tenant_key + ":" + sha256(phone_e164)`, a record containing OTP_Hash, `tenant_key`, `phone_e164`, `user_id`, `created_at_utc`, `expires_at_utc = created_at_utc + OtpTtlSeconds`, `attempt_count = 0`, AND SHALL set the cache entry's absolute expiration to `expires_at_utc`.
12. WHEN the OTP record is stored, THE Sms_Sender SHALL be invoked with the E164 number and the body template `"Mã đăng nhập của bạn: {otp}. Mã có hiệu lực trong {ttl_minutes} phút."` localized through `IViewLocalizer`.
13. WHEN Sms_Sender returns success, THE Phone_Login_Controller SHALL respond with HTTP 302 redirect to `/Account/LoginWithPhone/Verify` AND SHALL preserve the `returnUrl` value as a query-string parameter on the redirect target AND SHALL set an opaque short-lived signed token (data-protection-protected) in a session cookie keyed `phone_otp_session` containing `tenant_key`, `phone_e164_hash`, `expires_at_utc`.
14. IF Sms_Sender throws or returns failure, THEN THE Phone_Login_Controller SHALL invalidate the just-created Otp_Store record, SHALL log at level Error, AND SHALL re-render Login_Page with the "Số điện thoại" tab pre-activated AND Generic_Error displayed.
15. THE Phone_Request_Page SHALL preserve `returnUrl` across re-render POST cycles (when validation fails) by re-emitting the hidden field with the originally submitted value AND, on the success path defined in clause 13, the HTTP 302 redirect target SHALL be `/Account/LoginWithPhone/Verify` carrying `returnUrl` as a query-string parameter so that Phone_Verify_Page is reached as a top-level navigation, not as an in-page panel swap.

### Requirement 4: Trang xác thực OTP — bước 2

**User Story:** As a user who has received an OTP via SMS, I want to enter the code on a dedicated verification page and be signed in if it is correct, so that I can continue to my originally requested OIDC client.

#### Acceptance Criteria

1. THE Phone_Verify_Page SHALL exist as a server-rendered view at `Views/Account/LoginWithPhone/Verify.cshtml` reachable via HTTP GET `/Account/LoginWithPhone/Verify`.
2. WHERE `PhoneOtpLogin:Enabled` is `false` or absent, THE GET endpoint `/Account/LoginWithPhone/Verify` SHALL return HTTP 404.
3. WHEN a user GETs `/Account/LoginWithPhone/Verify` without a valid `phone_otp_session` cookie, THE Phone_Login_Controller SHALL redirect (HTTP 302) to `/Account/Login` preserving `returnUrl` as a query-string parameter.
4. THE Phone_Verify_Page SHALL render an OTP input (`type="text"`, `inputmode="numeric"`, `autocomplete="one-time-code"`, `maxlength` equal to `OtpLength`), the masked phone number with only the last 4 digits visible, a hidden `ReturnUrl` field populated from the query string, an anti-forgery token, a primary submit button, a "Resend OTP" button that POSTs to `/Account/LoginWithPhone/Resend` with the cooldown remaining displayed in seconds, AND a back-link element (`<a>`) pointing to `/Account/Login` that preserves `returnUrl`.
5. WHEN a user submits the Phone_Verify_Page form, THE Phone_Login_Controller SHALL read `tenant_key` and `phone_e164_hash` from the `phone_otp_session` cookie, SHALL load the matching record from Otp_Store, AND SHALL increment `attempt_count` atomically before comparing the submitted OTP.
6. IF no Otp_Store record exists for the session OR `now_utc > expires_at_utc`, THEN THE Phone_Login_Controller SHALL re-render Phone_Verify_Page with Generic_Verify_Error.
7. IF `attempt_count > MaxVerifyAttemptsPerOtp`, THEN THE Phone_Otp_Service SHALL delete the Otp_Store record AND THE Phone_Login_Controller SHALL re-render Phone_Verify_Page with Generic_Verify_Error.
8. WHEN comparing the submitted OTP to OTP_Hash, THE Phone_Otp_Service SHALL use a constant-time equality check (`CryptographicOperations.FixedTimeEquals` or equivalent) over HMAC-SHA256 outputs.
9. IF the submitted OTP does not match, THEN THE Phone_Login_Controller SHALL re-render Phone_Verify_Page with Generic_Verify_Error.
10. WHEN the submitted OTP matches, THE Phone_Otp_Service SHALL delete the Otp_Store record before any further action AND SHALL clear the `phone_otp_session` cookie.
11. WHEN the OTP matches, THE Phone_Login_Controller SHALL load the User_Identity associated with the matching record, SHALL invoke `ApplicationSignInManager.SignInAsync(user, isPersistent: false)`, AND SHALL raise `Duende.IdentityServer.Events.UserLoginSuccessEvent` with `loginType = "phone-otp"`.
12. WHEN sign-in succeeds AND `returnUrl` resolves to a valid `Duende.IdentityServer` authorization context (`IIdentityServerInteractionService.GetAuthorizationContextAsync`), THE Phone_Login_Controller SHALL redirect to `returnUrl` exactly as `AccountController.Login` does after password sign-in (including the native-client `LoadingPage("Redirect", returnUrl)` branch).
13. WHEN sign-in succeeds AND `returnUrl` is null OR does not resolve to an authorization context, THE Phone_Login_Controller SHALL redirect to `~/` using the same `RedirectToLocalAsync` helper used by Account_Controller.
14. THE Phone_Verify_Page form POST endpoint SHALL include `[ValidateAntiForgeryToken]` AND SHALL accept POST only.

### Requirement 5: Resend OTP

**User Story:** As a user who did not receive the SMS, I want to request a new OTP without restarting the flow, so that I can retry without losing my `returnUrl` context.

#### Acceptance Criteria

1. WHEN a user POSTs to `/Account/LoginWithPhone/Resend` without a valid `phone_otp_session` cookie, THE Phone_Login_Controller SHALL redirect (HTTP 302) to `/Account/Login` preserving `returnUrl`.
2. IF the time since the last OTP issuance for the resolved `phone_e164` is less than `ResendCooldownSeconds`, THEN THE Phone_Login_Controller SHALL re-render Phone_Verify_Page with the existing cooldown value AND SHALL NOT invoke Sms_Sender.
3. WHEN the resend cooldown has elapsed, THE Phone_Login_Controller SHALL execute the same OTP generation, storage, and SMS-send sequence defined in Requirement 3 (clauses 10 through 14) AND SHALL replace any existing Otp_Store record for the same key.
4. WHEN a resend succeeds, THE Phone_Login_Controller SHALL re-render Phone_Verify_Page AND SHALL reset `attempt_count` to 0.
5. THE Resend endpoint SHALL include `[ValidateAntiForgeryToken]` AND SHALL accept POST only.

### Requirement 6: Rate limit và chống brute-force

**User Story:** As an STS operator, I want hard limits on OTP issuance and verification per phone and per IP, so that abuse and brute-force attempts are mitigated.

#### Acceptance Criteria

1. THE Phone_Otp_Service SHALL allow at most one OTP issuance per `phone_e164` per `ResendCooldownSeconds` window.
2. THE Phone_Otp_Service SHALL allow at most `IpRateLimitMaxRequests` OTP issuance requests per remote IP per `IpRateLimitWindowSeconds` rolling window.
3. THE Phone_Otp_Service SHALL allow at most `MaxVerifyAttemptsPerOtp` verify attempts per Otp_Store record before the record is deleted.
4. THE Phone_Otp_Service SHALL track failed verify attempts per `phone_e164` AND, IF total failures across OTP records exceed `PhoneVerifyLockoutMaxFailures` within `PhoneVerifyLockoutWindowSeconds`, THEN THE Phone_Otp_Service SHALL reject all OTP issuance requests for that `phone_e164` until the lockout window expires.
5. THE Phone_Login_Controller SHALL resolve the remote IP from `HttpContext.Connection.RemoteIpAddress` AND, WHERE `ForwardedHeadersConfiguration:Enabled` is `true`, SHALL trust the value already canonicalized by the existing forwarded-headers middleware.
6. WHEN any rate-limit check rejects a request, THE Phone_Otp_Service SHALL emit a log entry at level Warning containing `Reason`, `TenantKey`, `PhoneLast4`, `RemoteIp`, AND SHALL NOT include the OTP value or full phone number.
7. THE Phone_Otp_Service rate-limit counters SHALL be stored in Otp_Store under keys distinct from OTP records (e.g. `"otp:rl:phone:" + sha256(phone_e164)` and `"otp:rl:ip:" + ip_hash`) with TTL equal to the corresponding window.

### Requirement 7: Phòng chống enumeration và đối xử nhất quán

**User Story:** As a security-conscious operator, I want all rejection paths in step 1 to be indistinguishable to an attacker, so that an attacker cannot learn whether a phone number is registered.

#### Acceptance Criteria

1. THE Phone_Login_Controller response body, HTTP status code, and visible error message SHALL be identical for the following step-1 rejection cases: invalid E164, number not registered, number registered but `PhoneNumberConfirmed = false`, phone-level rate-limit exceeded, IP-level rate-limit exceeded, phone-level lockout active, honeypot field non-empty, missing tenant context, Twilio send failure.
2. THE Phone_Login_Controller SHALL apply the randomized delay defined in Requirement 3.8 to all step-1 rejection cases listed in Requirement 7.1.
3. THE Phone_Login_Controller SHALL NOT include `phone-not-registered`, `phone-not-confirmed`, `rate-limit`, `lockout`, `honeypot-tripped`, or any reason-revealing marker in HTTP headers, response cookies, or HTML body returned to the client.
4. THE Phone_Login_Controller SHALL emit log entries at server side that distinguish the rejection reasons via structured properties, AND THE log entries SHALL contain only the last 4 digits of the phone number.
5. THE indistinguishable rejection response SHALL re-render Login_Page with the "Số điện thoại" tab pre-activated server-side AND Generic_Error displayed inside the Phone_Request_Page's validation area, so that the visible HTTP response body remains consistent across rejection branches.

### Requirement 8: Multi-tenant scoping

**User Story:** As a tenant administrator, I want phone-OTP login to authenticate users only inside the tenant resolved from the request host, so that a user belonging to tenant A cannot sign in via tenant B's STS hostname.

#### Acceptance Criteria

1. THE Phone_Login_Controller SHALL read the current tenant from `ITenantContextAccessor.Current` AND SHALL NOT accept any tenant identifier from the request body or query string.
2. WHEN the Phone_Otp_Service queries User_Identity, THE query SHALL filter by `TenantKey == current_tenant_key` in addition to `PhoneNumber == normalized AND PhoneNumberConfirmed == true`.
3. THE Otp_Store key for an OTP record SHALL include the `tenant_key` so that two users with the same phone in two tenants cannot collide.
4. THE `phone_otp_session` cookie payload SHALL include `tenant_key`, AND IF the `tenant_key` in the cookie does not match `ITenantContextAccessor.Current.TenantKey` at verify time, THEN THE Phone_Login_Controller SHALL clear the cookie AND redirect to `/Account/Login` preserving `returnUrl`.

### Requirement 9: Lưu trữ OTP và bí mật

**User Story:** As a security reviewer, I want OTPs to never sit in plaintext at rest and to be invalidated on every consume, so that database or cache compromise cannot replay codes.

#### Acceptance Criteria

1. THE Phone_Otp_Service SHALL store OTP_Hash = HMAC-SHA256(otp_plaintext, server_secret) in Otp_Store, AND SHALL NOT persist the OTP plaintext in cache, database, or log.
2. THE server_secret used for OTP_Hash SHALL be sourced from ASP.NET Core Data Protection (`IDataProtectionProvider.CreateProtector("PhoneOtp.HashKey")`) and SHALL persist across STS_Host restarts via the existing data-protection store (already wired to `IdentityServerDataProtectionDbContext`).
3. THE Phone_Otp_Service SHALL set the Otp_Store cache entry absolute expiration to `OtpTtlSeconds`, AND THE STS_Host SHALL rely on Redis TTL for ephemeral cleanup.
4. THE Phone_Otp_Service SHALL delete an Otp_Store record immediately after a successful verify or after `MaxVerifyAttemptsPerOtp` is reached, before returning the response.
5. THE STS_Host SHALL use the Redis instance already configured in `TenantInfrastructure:RedisInstanceName` AND SHALL apply the prefix `"otp:"` to all phone-OTP keys to avoid collision with the existing `"tenant-registry:"` instance prefix.

### Requirement 10: SMS sender abstraction và Twilio integration

**User Story:** As a developer, I want SMS sending behind an interface so that tests do not call Twilio and so that the provider can be swapped, so that the codebase stays testable and pluggable.

#### Acceptance Criteria

1. THE STS_Host SHALL define an interface `ISmsSender` in namespace `Skoruba.Duende.IdentityServer.STS.Identity.Services` with a single async method `SendAsync(string e164PhoneNumber, string body, CancellationToken cancellationToken)` returning `Task<SmsSendResult>` (a result type carrying `Succeeded`, `ProviderMessageId`, `ErrorCode`, `ErrorMessage`).
2. THE STS_Host SHALL provide a Twilio_Sms_Sender implementation that uses the official Twilio .NET SDK (`Twilio` NuGet package) AND SHALL read `AccountSid`, `AuthToken`, `FromNumber` from `SmsConfiguration:Twilio`.
3. THE Twilio_Sms_Sender SHALL apply a per-call timeout of 2 seconds AND SHALL retry exactly once on transient failures (network errors, HTTP 5xx, Twilio error codes documented as transient).
4. IF the Twilio_Sms_Sender fails after the retry, THEN THE method SHALL return `SmsSendResult.Failed(...)` AND SHALL NOT throw to the caller.
5. THE STS_Host SHALL register Twilio_Sms_Sender as `ISmsSender` only WHEN `PhoneOtpLogin:Enabled = true` AND Twilio configuration is complete; otherwise THE STS_Host SHALL register Fake_Sms_Sender (which records messages in memory for tests).
6. THE Twilio_Sms_Sender SHALL log every send attempt at level Information (with redacted phone) AND every failure at level Error, AND SHALL NOT log `AuthToken` or message body containing the OTP.
7. WHEN `PhoneOtpLogin:Enabled = true`, THE STS_Host startup SHALL register a hosted health check that does not call Twilio at request time but SHALL surface `SmsConfiguration:Twilio` completeness via the existing health-check endpoint already registered for the host.

### Requirement 11: Provisioning policy

**User Story:** As a tenant administrator, I want phone-OTP login to refuse to log in any user that I have not explicitly provisioned, so that an attacker cannot create accounts merely by guessing phone numbers.

#### Acceptance Criteria

1. THE Phone_Login_Controller SHALL NOT create, modify, or auto-provision any User_Identity, role, or claim during the OTP flow.
2. IF the resolved User_Identity has `PhoneNumberConfirmed == false`, THEN THE Phone_Login_Controller SHALL treat the request as Requirement 7.1 indistinguishable rejection.
3. IF the resolved User_Identity has `LockoutEnd > now_utc`, THEN THE Phone_Login_Controller SHALL treat the request as Requirement 7.1 indistinguishable rejection.
4. THE Phone_Login_Controller SHALL respect any sign-in restriction enforced by the existing `EnsureLoginAllowedAsync` and `EnsureClientAllowedAsync` checks in Account_Controller AND SHALL invoke equivalent checks before issuing the cookie.

### Requirement 12: Coexistence với username/password và OIDC continuation

**User Story:** As a downstream OIDC client, I want to continue receiving the same authentication cookie and user claims regardless of which method the user used to sign in, so that downstream clients require no changes.

#### Acceptance Criteria

1. THE STS_Host SHALL NOT modify the existing Identity cookie scheme name, expiration policy, or `SignInScheme` registration as part of this feature.
2. THE STS_Host SHALL NOT modify IdentityServer signing keys, validation keys, scope definitions, or token lifetimes as part of this feature.
3. WHEN phone-OTP sign-in succeeds, THE Phone_Login_Controller SHALL invoke `ApplicationSignInManager.SignInAsync` so that the cookie issued is byte-equivalent (in scheme and properties, not in value) to the cookie issued by the existing username/password flow for the same user.
4. WHEN phone-OTP sign-in succeeds AND `returnUrl` is an IdentityServer authorization endpoint, THE Phone_Login_Controller SHALL hand off to the same `Redirect(returnUrl)` / `LoadingPage("Redirect", returnUrl)` branches that `AccountController.Login` uses, including the native-client branch.
5. THE existing username/password form SHALL remain functionally and visually unchanged when `PhoneOtpLogin:Enabled = true`; the tab control wraps the existing form into the "Tài khoản" panel without modifying its inputs, validation summary, action URL, model binding, or styling beyond the panel-level container element required by Requirement 2.
6. WHERE `ServerSideSessionsConfiguration:Enabled = true`, THE phone-OTP sign-in SHALL produce a server-side session record identical in shape to one produced by the username/password flow.
7. THE STS_Host SHALL NOT delete, rename, or move `Views/Account/Login.cshtml`; modifications to this file SHALL be additive (insertion of tab markup wrapper and Phone_Request_Page).

### Requirement 13: Audit và logging

**User Story:** As an operator, I want every OTP request, verify, and sign-in to be logged with redacted phone numbers, so that I can audit and triage abuse without leaking PII.

#### Acceptance Criteria

1. WHEN a step-1 OTP request arrives, THE Phone_Login_Controller SHALL emit a Serilog log entry at level Information containing `Event="PhoneOtpRequest"`, `TenantKey`, `PhoneLast4`, `RemoteIp`, `Outcome` (`Issued`, `Rejected`).
2. WHEN a verify attempt arrives, THE Phone_Login_Controller SHALL emit a Serilog log entry at level Information containing `Event="PhoneOtpVerify"`, `TenantKey`, `PhoneLast4`, `RemoteIp`, `AttemptCount`, `Outcome` (`Succeeded`, `Mismatch`, `Expired`, `Exhausted`).
3. WHEN a phone-OTP sign-in succeeds, THE Phone_Login_Controller SHALL raise `Duende.IdentityServer.Events.UserLoginSuccessEvent` with `LoginType="phone-otp"`.
4. THE Phone_Login_Controller SHALL NOT log the OTP plaintext, the OTP_Hash, the full phone number, the Twilio message body, or the Twilio AuthToken at any level.
5. WHEN a Twilio call fails, THE Twilio_Sms_Sender SHALL emit a Serilog log entry at level Error containing `Provider="twilio"`, `ProviderErrorCode`, `PhoneLast4`, AND SHALL NOT include the OTP or message body.

### Requirement 14: Anti-CSRF, anti-bot, accessibility

**User Story:** As a user, I want the phone-OTP pages to be safe to use from any browser and accessible, so that they meet the same UX bar as the existing login page.

#### Acceptance Criteria

1. THE Phone_Request_Page (inside Login_Page), Phone_Verify_Page form, and Resend endpoint SHALL all enforce ASP.NET Core anti-forgery via `[ValidateAntiForgeryToken]` on every POST.
2. THE Phone_Request_Page SHALL include a hidden honeypot input named `website` (visually hidden, `tabindex="-1"`, `autocomplete="off"`) AND IF that input is non-empty on POST, THEN THE Phone_Login_Controller SHALL respond with the indistinguishable rejection from Requirement 7.1.
3. THE Login_Page (both panels) and Phone_Verify_Page SHALL render Vietnamese strings via `IViewLocalizer` keys `LoginWithPhone.TabAccount`, `LoginWithPhone.TabPhone`, `LoginWithPhone.PhoneLabel`, `LoginWithPhone.RequestSubmit`, `LoginWithPhone.OtpLabel`, `LoginWithPhone.VerifySubmit`, `LoginWithPhone.Resend`, `LoginWithPhone.BackToLogin`, `LoginWithPhone.GenericError`, `LoginWithPhone.GenericVerifyError`.
4. THE Phone_Request_Page and Phone_Verify_Page form inputs SHALL have `<label for="...">` association, `inputmode="tel"` for phone, `inputmode="numeric"` and `autocomplete="one-time-code"` for OTP, AND SHALL preserve focus management consistent with the existing Login_Page.
5. WHERE the operator decides to enable CAPTCHA in the future, THE Phone_Login_Controller SHALL expose an extension point (interface `IPhoneOtpAntiBotChallenge`) but SHALL NOT require a CAPTCHA in the initial release.
6. THE tab control rendered by Requirement 2 SHALL meet WAI-ARIA Authoring Practices for tabs (single tabstop into the tablist, ArrowLeft/ArrowRight to switch active tab, Home/End optional but allowed, panels labelled by their tab, panels focusable with `tabindex="0"`).

### Requirement 15: Data Protection và secrets sourcing

**User Story:** As a security reviewer, I want Twilio credentials and the OTP HMAC key to be sourced from the same configuration providers already wired in the host, so that secret management remains centralized.

#### Acceptance Criteria

1. THE STS_Host SHALL read `SmsConfiguration:Twilio:AccountSid`, `SmsConfiguration:Twilio:AuthToken`, `SmsConfiguration:Twilio:FromNumber` through the standard `IConfiguration` pipeline AND SHALL respect any Azure Key Vault provider already registered in `Startup.cs`.
2. THE STS_Host SHALL NOT read Twilio credentials from any path outside `IConfiguration`.
3. THE Phone_Otp_Service HMAC key SHALL be derived from `IDataProtectionProvider` and SHALL NOT be stored in `appsettings.json` plaintext.
4. THE STS_Host SHALL fail-fast at startup IF `IDataProtectionProvider` cannot produce a protector (e.g., because `IdentityServerDataProtectionDbContext` is unavailable) AND `PhoneOtpLogin:Enabled = true`.

### Requirement 16: Testing acceptance

**User Story:** As a developer, I want automated tests to cover the OTP service, the SMS abstraction, the controller branches, the tab UI rendering, and the configuration flag, so that regressions are caught in CI without calling Twilio.

#### Acceptance Criteria

1. THE solution SHALL contain unit tests for Phone_Otp_Service that exercise: OTP generation length, HMAC hashing, expiry, attempt-counter increment-and-delete-on-exhaustion, constant-time compare branch, tenant scoping.
2. THE solution SHALL contain unit tests for the SMS abstraction using Fake_Sms_Sender that assert: Phone_Login_Controller invokes Sms_Sender exactly once per non-rate-limited request AND zero times for every rate-limited or invalid request branch.
3. THE solution SHALL contain unit tests for rate-limit branches per phone, per IP, and per-phone lockout window.
4. THE solution SHALL contain integration tests that boot the STS_Host with `PhoneOtpLogin:Enabled = false` AND assert: GET `/Account/Login` returns HTML that does NOT contain a `role="tablist"` element AND does NOT contain the Phone_Request_Page fields (`name="website"`, `name="PhoneNumber"`); GET `/Account/LoginWithPhone/Verify` returns HTTP 404; POST `/Account/LoginWithPhone/Request` and POST `/Account/LoginWithPhone/Resend` return HTTP 404.
5. THE solution SHALL contain integration tests that boot the STS_Host with `PhoneOtpLogin:Enabled = true` AND Fake_Sms_Sender registered AND assert: GET `/Account/Login` renders both panels server-side, the "Tài khoản" tab is the active one (`aria-selected="true"`), the "Số điện thoại" panel carries the `hidden` attribute on initial render, the username/password form retains its original `id="local-login-form"`, action route, and inputs unchanged; POST `/Account/LoginWithPhone/Request` with a valid number redirects (302) to `/Account/LoginWithPhone/Verify?returnUrl=...`; GET `/Account/LoginWithPhone/Verify` renders.
6. THE solution SHALL contain a parser/normalizer round-trip test ensuring `Normalize(Format(e164)) == e164` for all generated valid numbers in region "VN".
7. THE test suite SHALL NOT make outbound network calls to Twilio in any configuration AND SHALL fail the build if a real Twilio credential is detected in test configuration.
8. THE solution SHALL contain at least one DOM/markup assertion test (via integration test reading the rendered HTML) that the tab buttons carry `role="tab"`, `aria-controls`, and `aria-selected`, and that the tabpanels carry `role="tabpanel"` and `aria-labelledby`.

### Requirement 17: Non-functional và compatibility

**User Story:** As an STS operator, I want this feature to introduce no change to existing token, session, or auth-scheme behavior, so that downstream clients and SSO continue to work without changes.

#### Acceptance Criteria

1. THE STS_Host SHALL preserve the existing `IdentityConstants.ApplicationScheme`, `IdentityConstants.ExternalScheme`, and JwtBearer scheme registrations unchanged.
2. THE STS_Host SHALL preserve `IdentityServerOptions`, `ServerSideSessionsConfiguration`, and cookie expiration values unchanged.
3. THE STS_Host SHALL preserve the existing `AddEmailSenders(Configuration)` call AND SHALL add a sibling `AddSmsSenders(Configuration)` call only WHEN `PhoneOtpLogin:Enabled = true`.
4. THE STS_Host startup ordering SHALL be: existing Identity registration → existing IdentityServer registration → existing AddEmailSenders → AddSmsSenders → AddAuthorizationPolicies, mirroring the placement of `AddEmailSenders` in `Startup.cs`.
5. THE feature SHALL respect the existing tenant resolution order; THE Phone_Login_Controller SHALL NOT bypass `TenantResolutionMiddleware`.
6. THE Login_Tabs_Asset (`wwwroot/js/login-tabs.js`, `wwwroot/css/login-tabs.css` or equivalent CSS hooks in `wwwroot/css/app.css`) SHALL be served by the existing static-files middleware AND SHALL be referenced from `Views/Account/Login.cshtml` only, not from `Views/Shared/_Layout.cshtml`.
