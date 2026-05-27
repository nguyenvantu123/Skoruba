# Requirements Document

Phone OTP Multi-Account Select

## Introduction

Tính năng này nới phạm vi hành vi của luồng đăng nhập phone-OTP đã có (`Skoruba.Duende.IdentityServer.STS.Identity`, spec `phone-otp-login`) để hỗ trợ trường hợp **một số điện thoại được gắn với nhiều `UserIdentity` cùng tenant**. Hành vi hiện tại trong `PhoneOtpService.IssueAsync` reject mọi case `users.Count != 1`, kể cả khi `users.Count > 1`. Mục tiêu của feature này là chỉ rẽ nhánh **sau khi user đã verify OTP thành công**: nếu chỉ có 1 candidate user → sign-in luôn (giữ nguyên UX); nếu có nhiều candidate → render trang chọn account; user click một account thì sign-in account đó.

Phạm vi và ranh giới quan trọng:

- **Anti-enumeration**: trang nhập OTP (`/Account/LoginWithPhone/Verify`) phải giống nhau byte-for-byte với case 1-user, không leak count, không có markup khác biệt, không có header/cookie khác biệt trước khi user submit OTP đúng. Một attacker không được suy ra số lượng user khớp số điện thoại trong tenant chỉ bằng cách quan sát phản hồi step-1 hoặc trang Verify.
- **OTP single-use**: khi verify thành công, record trong Otp_Store **bị xoá ngay lập tức** (giữ nguyên hợp đồng R9.4 của spec `phone-otp-login`); account-select phải dùng một context riêng (Account_Select_Context) gắn vào cookie session ngắn hạn — KHÔNG tái sử dụng OTP, KHÔNG cho phép submit lại OTP để chọn account khác.
- **Candidate set immutability**: danh sách candidate userIds được lock-in tại thời điểm issue OTP và lưu vào cookie sau khi verify thành công. Thao tác account-select chỉ được phép chọn user nằm trong tập đó. Nếu DB thay đổi giữa hai bước (user mới được tạo, hoặc một candidate bị disable), account-select KHÔNG được mở rộng tập user, chỉ filter ra user đã invalid.
- **Tenant scoping**: candidate set được scope theo `tenant_key` resolve từ `ITenantContextAccessor`; account-select cookie chứa `tenant_key` và mọi request select đều cross-check tenant key hiện tại.
- **Cookie codec**: tái sử dụng cơ chế ASP.NET Core Data Protection (`PhoneOtpSessionCookieCodec` pattern) cho cookie account-select; payload signed + encrypted; TTL ≤ 60 giây kể từ verify thành công.
- **UI form factor**: trang account-select là một `<form method="post">` server-rendered duy nhất chứa một HTML `<select>` dropdown liệt kê TẤT CẢ candidate (không có truncation, không có "+N more"), một anti-forgery token, một hidden `ReturnUrl`, và một nút submit "Tiếp tục". KHÔNG còn rendering kiểu list-of-cards với một form per candidate.
- **Privacy / username disclosure**: visible text của mỗi `<option>` trong dropdown là `UserIdentity.UserName` raw (không mask, không email, không role, không avatar). Việc hiển thị tập username gắn với một số điện thoại trong cùng tenant chỉ xảy ra **sau khi user đã verify OTP thành công** (proof-of-possession của số điện thoại). Đây là chủ ý design (yêu cầu nghiệp vụ) chứ KHÔNG phải PII leak: trang Verify (`/Account/LoginWithPhone/Verify`) vẫn KHÔNG được leak count hay username trước khi OTP đúng được submit (R3 không đổi). Trang Verify vẫn giữ nguyên anti-enumeration; trang chooser là điểm duy nhất nơi username trong Candidate_Set được render.
- **Rate limit / lockout**: sign-in fail tại bước account-select (vd user bị lockout sau khi user đã chọn) phải tính vào `PhoneVerifyLockoutMaxFailures` counter, hoặc một counter song song có cùng ngữ nghĩa, để không tạo bypass cho lockout. Đồng thời POST `/SelectAccount` chịu một per-IP rate-limit độc lập (R18) tái sử dụng `IPhoneOtpRateLimiter` để ngăn brute-force `SelectionToken` từ cùng một IP.
- **Backward compatible**: khi `users.Count == 1`, end-user trải nghiệm KHÔNG đổi (verify OTP → redirect về `returnUrl`). Account-select view không được render trong case này.
- **Feature flag**: thêm sub-flag `PhoneOtpLogin:MultiAccount:Enabled` (default `false`) để rollout từng môi trường mà không phá vỡ behaviour hiện tại trong production.

Out-of-scope (sẽ KHÔNG làm trong feature này):

- Đăng nhập SSO / external provider (Google, Microsoft, Twilio Verify) — không xử lý merge / linked-accounts.
- Đổi cách lưu phone number (không thêm constraint unique, không migration), không refactor `BuildPhoneLookupCandidates` heuristic ngoài phạm vi cần thiết.
- Tab UI / eye-toggle password / tab-control trên `/Account/Login` (đã thuộc spec `phone-otp-login`).
- Account merging, primary-account flagging, hoặc admin tooling để sửa duplicate phone trong DB (sẽ làm spec riêng nếu cần).
- "Remember last selected account" / persist lựa chọn account ở client storage.

## Glossary

- **STS_Host**: Tiến trình `Skoruba.Duende.IdentityServer.STS.Identity` — IdentityServer host nơi UI đăng nhập và endpoint OTP cư trú.
- **Phone_Login_Controller**: Controller MVC hiện có `Controllers/PhoneLoginController.cs` xử lý route `/Account/LoginWithPhone/{Request,Verify,Resend}`. Feature này MỞ RỘNG controller bằng cách thêm action mới (account-select GET/POST) và sửa nhánh continuation sau verify; KHÔNG được đổi route hiện có, KHÔNG được đổi cookie scheme hiện có.
- **Phone_Otp_Service**: Service `PhoneOtp/Services/PhoneOtpService.cs` (`IPhoneOtpService`). Feature này SỬA `IssueAsync` để chấp nhận nhánh `users.Count > 1` và mở rộng `IssueOtpResult` mang theo `CandidateUserIds`; KHÔNG thay đổi hợp đồng `VerifyAsync`.
- **OTP**: One-Time Password — mã số dùng một lần, đã được hash bằng HMAC-SHA256 trong Otp_Store. Định nghĩa giữ nguyên với spec `phone-otp-login` (R9).
- **Otp_Store**: Distributed cache (Redis) lưu `OtpStoreRecord`. Feature này MỞ RỘNG record để lưu `CandidateUserIds: IReadOnlyList<string>` và một field `PrimaryUserId` (là `UserId` đã có) bằng `null` khi `CandidateUserIds.Count > 1`.
- **Candidate_Set**: Tập các `UserIdentity.Id` thoả mãn `(PhoneNumber match BuildPhoneLookupCandidates(...) AND PhoneNumberConfirmed = true AND TenantKey = current_tenant_key)` tại thời điểm `IssueAsync`. Thứ tự được sắp xếp deterministic theo `(LockoutEnabled ASC, LockoutEnd NULL FIRST, NormalizedUserName ASC)` để hiển thị stable trên UI.
- **Account_Select_Context**: Bản ghi short-lived chứa `(TenantKey, PhoneE164Hash, CandidateUserIds, IssuedAtUtc, ExpiresAtUtc, OtpRecordId)` được lưu trong cookie `phone_otp_account_select` sau khi verify OTP thành công. TTL = `MultiAccount:SelectTtlSeconds` (mặc định 60s, range hợp lệ 30..180s).
- **Account_Select_Cookie**: Cookie HTTP-only, Secure, SameSite=Lax tên `phone_otp_account_select` chứa Account_Select_Context được protect/sign bằng `IDataProtectionProvider.CreateProtector("PhoneOtp.AccountSelectCookie")`. Riêng biệt với `phone_otp_session` (cookie step-1).
- **Account_Select_Page**: View server-rendered `Views/Account/LoginWithPhone/SelectAccount.cshtml` tại URL `/Account/LoginWithPhone/SelectAccount`, render dropdown chọn candidate. KHÔNG có client-side state, KHÔNG có AJAX, toàn trang là một `<form method="post">` duy nhất chứa một HTML `<select>` (xem `Candidate_Option`) và một nút submit; mỗi `<option>` gắn `value = SelectionToken` (xem R6).
- **Candidate_Option**: Phần markup của một candidate trong dropdown — một HTML `<option>` element có `value` = `SelectionToken` (per-candidate opaque token, xem R6) và visible text = `UserIdentity.UserName` raw (không mask, không email, không role, không avatar). KHÔNG render thêm bất kỳ field nào khác (không full email, không full phone, không user-id, không last-login, không IP, không claim, không role).
- **Phone_Verify_Page**: View hiện có `Views/Account/LoginWithPhone/Verify.cshtml`. Feature này KHÔNG được sửa markup ngoài việc cho phép template cộng thêm `data-*` attribute trung lập; render giống nhau cho mọi giá trị `CandidateUserIds.Count` ≥ 1.
- **Phone_Otp_Configuration**: Section cấu hình `PhoneOtpLogin` đã có. Feature này thêm sub-section `MultiAccount` với các key: `Enabled` (bool, default `false`), `SelectTtlSeconds` (int, default `60`, range `[30, 180]`), `IpSelectRateLimitWindowSeconds` (int, default `600`, range `[60, 3600]`), `IpSelectRateLimitMaxRequests` (int, default `30`, range `[5, 200]`).
- **Tenant_Context**: `TenantContext` truy cập qua `ITenantContextAccessor.Current`. Feature này KHÔNG thay đổi cách resolve tenant.
- **User_Identity**: Entity `Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity.UserIdentity` — đã có `PhoneNumber`, `PhoneNumberConfirmed`, `TenantKey`, `LockoutEnabled`, `LockoutEnd`.
- **Application_Sign_In_Manager**: `ApplicationSignInManager<UserIdentity>` — dùng để issue Identity cookie sau khi user pick một account.
- **Generic_Error**: Thông báo lỗi đã định nghĩa trong spec `phone-otp-login` cho rejection step-1: "Không thể gửi mã OTP. Vui lòng thử lại sau ít phút.". Feature này tái sử dụng cùng chuỗi.
- **Generic_Verify_Error**: Thông báo lỗi đã định nghĩa cho verify thất bại: "Mã OTP không đúng hoặc đã hết hạn.". Feature này tái sử dụng cùng chuỗi.
- **Account_Select_Expired_Error**: Thông báo lỗi mới khi cookie account-select hết hạn hoặc invalid lúc user POST chọn account: "Phiên chọn tài khoản đã hết hạn. Vui lòng nhập lại mã OTP." (vi) / "Account selection session expired. Please request a new OTP." (en).
- **Account_Select_Generic_Error**: Thông báo lỗi mới cho mọi rejection account-select không-do-hết-hạn (user-id không thuộc candidate set, user vừa bị disable / lockout, tenant mismatch trên cookie): "Không thể đăng nhập với tài khoản đã chọn. Vui lòng thử lại." (vi) / "Cannot sign in with the selected account. Please try again." (en). Phải giống nhau cho mọi nhánh để không leak lý do reject.
- **Phone_Last4**: 4 chữ số cuối của E164 phone — đã dùng cho logging trong spec gốc.
- **Phone_Sha8**: 8 ký tự đầu của SHA-256 hex của E164 phone — đã dùng cho logging trong spec gốc.
- **User_Id_Hash**: SHA-256 hex (8 ký tự đầu) của `UserIdentity.Id` — dùng cho logging trong feature này, KHÔNG được phép log `UserIdentity.Id` raw.
- **Return_Url**: Tham số `returnUrl` mà Account_Controller / Phone_Login_Controller hiện đang dùng để continuation IdentityServer authorization context.

## Requirements

### Requirement 1: Cấu hình bật/tắt multi-account select

**User Story:** As an STS operator, I want a sub-feature flag for the multi-account-select branch so that I can roll it out per-environment without changing the existing single-user phone-OTP behaviour.

#### Acceptance Criteria

1. THE STS_Host SHALL read configuration value `PhoneOtpLogin:MultiAccount:Enabled` (boolean) from `appsettings.json` and environment variables on startup.
2. WHERE `PhoneOtpLogin:Enabled` is `false` or absent, THE STS_Host SHALL ignore `PhoneOtpLogin:MultiAccount:Enabled` AND THE Account_Select_Page route SHALL return HTTP 404.
3. WHERE `PhoneOtpLogin:MultiAccount:Enabled` is `false` or absent, THE Phone_Otp_Service SHALL preserve the legacy behaviour AND SHALL reject `IssueOtpRequest` whenever the user lookup returns more than one candidate (current `users.Count != 1` rejection branch).
4. WHERE `PhoneOtpLogin:MultiAccount:Enabled` is `true`, THE Phone_Otp_Service SHALL accept `users.Count >= 1` (zero is still rejected as today) AND SHALL persist the resulting Candidate_Set in Otp_Store.
5. THE STS_Host SHALL apply the default values `MultiAccount:SelectTtlSeconds = 60`, `MultiAccount:IpSelectRateLimitWindowSeconds = 600`, AND `MultiAccount:IpSelectRateLimitMaxRequests = 30` WHEN the corresponding configuration keys are absent.
6. IF `MultiAccount:SelectTtlSeconds` is configured outside the inclusive range `[30, 180]`, THEN THE STS_Host SHALL fail-fast at startup with an exception naming the configuration key.
7. IF `MultiAccount:IpSelectRateLimitWindowSeconds` is configured outside the inclusive range `[60, 3600]` OR `MultiAccount:IpSelectRateLimitMaxRequests` is configured outside the inclusive range `[5, 200]`, THEN THE STS_Host SHALL fail-fast at startup with an exception naming the configuration key.
8. THE STS_Host SHALL register the Account_Select_Page route AND the Account_Select_Cookie codec only WHEN `PhoneOtpLogin:Enabled = true` AND `PhoneOtpLogin:MultiAccount:Enabled = true`; under any other combination, GET/POST on `/Account/LoginWithPhone/SelectAccount` SHALL return HTTP 404.

### Requirement 2: Mở rộng Phone_Otp_Service.IssueAsync để hỗ trợ nhiều candidate

**User Story:** As a tenant user with multiple accounts sharing the same phone number, I want the system to issue an OTP whenever at least one of my accounts matches, so that I can continue the sign-in flow regardless of how many accounts I have.

#### Acceptance Criteria

1. WHEN `MultiAccount:Enabled = true` AND the user lookup query returns `users.Count >= 1`, THE Phone_Otp_Service SHALL proceed to OTP generation for the matched Candidate_Set instead of rejecting.
2. WHEN `MultiAccount:Enabled = false` OR the user lookup returns `users.Count == 0`, THE Phone_Otp_Service SHALL preserve the existing rejection branch (no SMS sent, indistinguishable rejection per Requirement 7 of `phone-otp-login`).
3. THE Phone_Otp_Service SHALL build the Candidate_Set as `users.Select(u => u.Id)` ordered deterministically by `(LockoutEnabled ASC, LockoutEnd NULL FIRST then ASC, NormalizedUserName ASC)`.
4. WHEN persisting the OtpStoreRecord, THE Phone_Otp_Service SHALL store the Candidate_Set under a new field `CandidateUserIds: IReadOnlyList<string>` AND SHALL leave the existing `UserId` field equal to `Candidate_Set[0]` for backward compatibility with single-user reads (so legacy code paths reading `record.UserId` continue to function).
5. WHEN `Candidate_Set.Count == 1`, THE Phone_Otp_Service SHALL behave byte-equivalently to today (single-user flow), including the SMS body content, the absolute expiration of the cache entry, and the rate-limit counter writes.
6. THE OtpStoreRecord serialization layout SHALL be backward compatible: a record persisted by the previous code (no `CandidateUserIds` field) SHALL deserialize successfully with `CandidateUserIds = [record.UserId]` so that an in-flight OTP issued before deployment continues to verify after deployment.
7. THE Phone_Otp_Service SHALL NOT include any indication of `Candidate_Set.Count` in the SMS body, in the cookie returned by step-1, or in any HTTP header / status code on the step-1 response.
8. THE Phone_Otp_Service SHALL emit a single Information-level log entry per issuance containing `Event="PhoneOtpRequest"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `Outcome="Issued"`, `CandidateCount` (numeric — server-side audit only, not exposed to client) AND SHALL NOT log any individual `UserIdentity.Id` from the Candidate_Set.

### Requirement 3: Anti-enumeration tại trang nhập OTP

**User Story:** As a security-conscious operator, I want the OTP entry page to look and behave identically whether one or many users matched, so that an attacker cannot infer account count from network traffic or rendered HTML.

#### Acceptance Criteria

1. THE Phone_Verify_Page (`Views/Account/LoginWithPhone/Verify.cshtml`) SHALL render with byte-for-byte identical markup, identical headers, identical cookies, and identical visible text regardless of `Candidate_Set.Count`, EXCEPT for fields that already vary today (`MaskedPhone`, anti-forgery token value, `cooldown` value).
2. THE Phone_Login_Controller `RequestOtp` HTTP 302 redirect to `/Account/LoginWithPhone/Verify` SHALL be the same status code, the same `Location` header pattern, and the same set of cookies on the response (only `phone_otp_session`) regardless of `Candidate_Set.Count`.
3. THE Phone_Login_Controller SHALL NOT set any cookie, query-string parameter, view-data key, or response header that encodes or reveals `Candidate_Set.Count` before the OTP is verified successfully.
4. THE `phone_otp_session` cookie payload SHALL NOT carry `Candidate_Set.Count`, candidate user-ids, or any boolean flag derived from them; it SHALL continue to carry only `(TenantKey, PhoneE164Hash, ExpiresAtUtc, Version)` as today.
5. THE Phone_Verify_Page subtitle, error messages, and aria-live regions SHALL NOT branch on `Candidate_Set.Count`.
6. THE Phone_Verify_Page form action, hidden inputs, and submit-button label SHALL be identical for all values of `Candidate_Set.Count`.
7. WHEN the verify response is rendered after a wrong OTP, THE Phone_Login_Controller SHALL display Generic_Verify_Error AND SHALL NOT change wording based on `Candidate_Set.Count`.

### Requirement 4: Rẽ nhánh sau verify OTP thành công

**User Story:** As a user with multiple matching accounts, I want to be presented with an account chooser only after I prove ownership of the phone number via the OTP, so that I can pick which account to sign in to.

#### Acceptance Criteria

1. WHEN `Phone_Otp_Service.VerifyAsync` returns `Outcome=Succeeded`, THE Phone_Login_Controller SHALL read `record.CandidateUserIds` (length ≥ 1) before deleting the OtpStoreRecord (the existing service contract already deletes the record on success — see R9.4 of `phone-otp-login`; this requirement clarifies the controller MUST capture `CandidateUserIds` from the verify pipeline before the record is removed from the cache).
2. WHEN `record.CandidateUserIds.Count == 1`, THE Phone_Login_Controller SHALL execute the existing single-user continuation: `ApplicationSignInManager.SignInAsync(user, isPersistent:false)`, raise `UserLoginSuccessEvent` with `LoginType="phone-otp"`, clear the `phone_otp_session` cookie, and redirect according to the existing `(GetAuthorizationContextAsync, IsNativeClient, IsLocalUrl)` cascade.
3. WHEN `record.CandidateUserIds.Count > 1` AND `MultiAccount:Enabled = false`, THE Phone_Login_Controller SHALL refuse the sign-in (this case is unreachable when the issue branch is gated correctly per R1.3, but the controller MUST treat it defensively as Generic_Verify_Error to avoid a fail-open).
4. WHEN `record.CandidateUserIds.Count > 1` AND `MultiAccount:Enabled = true`, THE Phone_Login_Controller SHALL NOT call `ApplicationSignInManager.SignInAsync` AND SHALL NOT raise `UserLoginSuccessEvent` AND SHALL clear the `phone_otp_session` cookie AND SHALL set the Account_Select_Cookie defined in Requirement 6 AND SHALL redirect (HTTP 302) to `/Account/LoginWithPhone/SelectAccount` preserving `returnUrl` as a query-string parameter.
5. THE Phone_Login_Controller SHALL emit a Serilog log entry at level Information containing `Event="PhoneOtpAccountSelectShown"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `CandidateCount` AND SHALL NOT log individual user-ids when redirecting to Account_Select_Page.
6. THE Phone_Login_Controller SHALL clear the `phone_otp_session` cookie before issuing the Account_Select_Cookie so that the two cookies do not coexist on the same response.

### Requirement 5: Account_Select_Page rendering

**User Story:** As a user who has just verified my OTP, I want a clear, accessible chooser page that lists all of my accounts in a single dropdown, so that I can select the correct account quickly with the keyboard or mouse.

#### Acceptance Criteria

1. THE Account_Select_Page SHALL exist as a server-rendered view at `Views/Account/LoginWithPhone/SelectAccount.cshtml` reachable via HTTP GET `/Account/LoginWithPhone/SelectAccount`.
2. WHEN a user GETs `/Account/LoginWithPhone/SelectAccount` without a valid Account_Select_Cookie, THE Phone_Login_Controller SHALL redirect (HTTP 302) to `/Account/Login` preserving `returnUrl` AND SHALL NOT render the chooser markup.
3. WHEN the Account_Select_Cookie payload's `TenantKey` does not match `ITenantContextAccessor.Current.TenantKey`, THE Phone_Login_Controller SHALL clear the cookie AND redirect (HTTP 302) to `/Account/Login` preserving `returnUrl`.
4. WHEN `now_utc > Account_Select_Context.ExpiresAtUtc`, THE Phone_Login_Controller SHALL clear the cookie AND redirect (HTTP 302) to `/Account/Login` preserving `returnUrl` AND SHALL emit a Warning log with `Event="PhoneOtpAccountSelectExpired"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`.
5. WHEN the Account_Select_Cookie is valid and unexpired, THE Account_Select_Page SHALL render exactly one `Candidate_Option` (HTML `<option>`) per `userId` in `Account_Select_Context.CandidateUserIds`, preserving the deterministic order produced by Requirement 2.3, with NO truncation: every candidate in the cookie payload SHALL appear in the dropdown.
6. WHERE a candidate's `UserIdentity` cannot be loaded at render time (deleted between issuance and selection), THE Account_Select_Page SHALL omit the corresponding `<option>` silently AND SHALL NOT mention deletion in the rendered HTML.
7. WHERE a candidate has `LockoutEnd > now_utc` at render time, THE Account_Select_Page SHALL still render the `<option>` without any "locked out" indicator (to avoid revealing per-account lockout state); the lockout SHALL be re-checked at POST time per Requirement 6.6 and rejected with Account_Select_Generic_Error.
8. THE visible text content of each `Candidate_Option` SHALL be exactly `UserIdentity.UserName` (raw, unmasked); THE Candidate_Option SHALL NOT include email, phone, role, role count, claim, avatar, last-login timestamp, IP, raw user-id, or any other field.
9. THE `value` attribute of each `Candidate_Option` SHALL be the per-candidate `SelectionToken` defined in Requirement 6, AND SHALL NOT contain `UserIdentity.Id` in plaintext.
10. THE Account_Select_Page SHALL render the chooser as a single `<form method="post">` element whose action posts to `/Account/LoginWithPhone/SelectAccount` AND whose body contains: an anti-forgery token; a hidden `ReturnUrl` field populated from the request query; a single `<select>` element with `name="SelectionToken"`, `id="account-select"`, `aria-required="true"`, containing the `Candidate_Option` elements from R5.5; a single submit button labelled with the localized key `LoginWithPhone.SelectAccount.Continue` (e.g. "Tiếp tục" / "Continue") that submits the form.
11. THE first `Candidate_Option` (in deterministic order from R2.3) SHALL be marked `selected` by default so that pressing the submit button without further interaction posts a valid `SelectionToken`.
12. THE Account_Select_Page SHALL NOT include any client-side JavaScript beyond the existing layout-level scripts, SHALL NOT make AJAX calls, AND SHALL NOT depend on jQuery; the dropdown SHALL function with the browser's native `<select>` behaviour only.
13. THE Account_Select_Page SHALL render a back-link `<a>` element pointing to `/Account/Login` preserving `returnUrl`, with the same accessibility / focus-management pattern as `Phone_Verify_Page`.
14. THE Account_Select_Page SHALL render a heading element with localized key `LoginWithPhone.SelectAccount.Title` AND a subtitle with key `LoginWithPhone.SelectAccount.Subtitle`.
15. WHERE `Account_Select_Context.CandidateUserIds.Count == 0` at render time (all candidates were deleted between issuance and selection, so the dropdown would be empty), THE Phone_Login_Controller SHALL NOT render the chooser markup, SHALL clear the Account_Select_Cookie, AND SHALL redirect (HTTP 302) to `/Account/Login` preserving `returnUrl` with TempData carrying Account_Select_Generic_Error (which the Login page already renders as Generic_Error per `phone-otp-login` Requirement 7.5).

### Requirement 6: Account_Select_Cookie và payload bảo vệ chống tampering / replay

**User Story:** As a security reviewer, I want the account-select cookie to be cryptographically bound to the verified OTP context, single-use per OTP, and short-lived, so that an attacker cannot replay it to sign in as a different user later.

#### Acceptance Criteria

1. THE Account_Select_Cookie name SHALL be `phone_otp_account_select` AND SHALL be set with `HttpOnly = true`, `Secure = true`, `SameSite = Lax`, `IsEssential = true`.
2. THE Account_Select_Cookie value SHALL be the output of `IDataProtectionProvider.CreateProtector("PhoneOtp.AccountSelectCookie").Protect(json)` where `json` is the camelCase JSON serialization of the Account_Select_Context payload.
3. THE Account_Select_Context payload SHALL contain exactly the fields `(TenantKey, PhoneE164Hash, CandidateUserIds: IReadOnlyList<string>, IssuedAtUtc, ExpiresAtUtc, OtpRecordKey, Version=1)` AND SHALL NOT contain plaintext phone numbers, plaintext OTP, or any user PII.
4. THE Account_Select_Cookie absolute expiration SHALL be set to `IssuedAtUtc + MultiAccount:SelectTtlSeconds`.
5. WHEN the Phone_Login_Controller sets the Account_Select_Cookie, THE controller SHALL also delete the `phone_otp_session` cookie in the same response, ensuring the two cookies never coexist.
6. WHEN a user POSTs to `/Account/LoginWithPhone/SelectAccount` with the Account_Select_Cookie, THE Phone_Login_Controller SHALL: (a) decode the cookie via the `PhoneOtp.AccountSelectCookie` protector and reject any payload that fails decryption / signature with Account_Select_Generic_Error; (b) verify `payload.ExpiresAtUtc > now_utc` and reject with Account_Select_Expired_Error otherwise; (c) verify `payload.TenantKey == ITenantContextAccessor.Current.TenantKey` and reject with Account_Select_Generic_Error otherwise; (d) verify the submitted `SelectionToken` (from the form) maps deterministically to a `userId` value that is contained in `payload.CandidateUserIds` (membership check) and reject with Account_Select_Generic_Error otherwise; (e) re-load the candidate `UserIdentity` from the database and re-apply `EnsureLoginAllowedAsync`-equivalent checks (`u.LockoutEnabled = false OR u.LockoutEnd <= now_utc`, `u.PhoneNumberConfirmed = true`, `u.TenantKey == current_tenant_key`) and reject with Account_Select_Generic_Error otherwise.
7. WHEN any rejection branch in Requirement 6.6 fires, THE Phone_Login_Controller SHALL increment the same per-phone failure counter that `Phone_Otp_Service` already uses for verify failures (`PhoneVerifyLockoutMaxFailures` / `PhoneVerifyLockoutWindowSeconds`) so that a brute-force attacker cannot bypass lockout by repeatedly POSTing to the account-select endpoint.
8. THE `SelectionToken` SHALL be a per-candidate opaque value computed as `HMAC-SHA256(IDataProtectionProvider.CreateProtector("PhoneOtp.AccountSelectToken").Protect(userId)`) — i.e. each candidate gets a token bound to (Account_Select_Cookie's protector key, userId). The token SHALL NOT contain the userId in plaintext AND SHALL be regenerated each time the page is rendered AND SHALL only be accepted within the lifetime of the Account_Select_Cookie that was current when the token was emitted.
9. WHEN a successful selection occurs, THE Phone_Login_Controller SHALL delete the Account_Select_Cookie before issuing the Identity cookie via `ApplicationSignInManager.SignInAsync(user, isPersistent:false)` so that the cookie cannot be replayed.
10. THE Account_Select_Cookie SHALL NOT be re-issued after a successful selection.
11. THE Account_Select_Cookie SHALL be deleted when the user navigates back to `/Account/Login` via the back-link defined in Requirement 5.12 AND `IsLocalUrl` returns true (so that abandoning the chooser does not leave a live cookie).
12. THE STS_Host SHALL fail-fast at startup IF `IDataProtectionProvider` cannot produce the `PhoneOtp.AccountSelectCookie` protector AND `PhoneOtpLogin:Enabled = true` AND `MultiAccount:Enabled = true`.

### Requirement 7: Sign-in continuation sau khi user pick account

**User Story:** As a user who selected an account, I want the system to sign me in as that account exactly the way the single-account flow does today, so that downstream OIDC clients see no behavioural difference.

#### Acceptance Criteria

1. WHEN every check in Requirement 6.6 passes, THE Phone_Login_Controller SHALL invoke `ApplicationSignInManager.SignInAsync(selectedUser, isPersistent: false)` for the resolved `UserIdentity`.
2. WHEN sign-in succeeds, THE Phone_Login_Controller SHALL raise `Duende.IdentityServer.Events.UserLoginSuccessEvent` with `LoginType = "phone-otp-multi"` AND with the `userId` of the selected user.
3. WHEN sign-in succeeds AND `returnUrl` resolves to a valid `Duende.IdentityServer` authorization context (`IIdentityServerInteractionService.GetAuthorizationContextAsync`), THE Phone_Login_Controller SHALL hand off to the same `Redirect(returnUrl)` / `LoadingPage("Redirect", returnUrl)` branches that the existing `Verify` action uses.
4. WHEN sign-in succeeds AND `returnUrl` is null OR does not resolve to an authorization context, THE Phone_Login_Controller SHALL redirect to `~/` using the same fallback branch the existing `Verify` action uses.
5. THE Phone_Login_Controller SHALL emit a Serilog log entry at level Information containing `Event="PhoneOtpAccountSelected"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `User_Id_Hash`, `Outcome="Succeeded"` AND SHALL NOT log the raw `UserIdentity.Id` or full email.
6. THE Phone_Login_Controller SHALL produce a server-side session record (where `ServerSideSessionsConfiguration:Enabled = true`) byte-equivalent in shape to the one produced by single-user phone-OTP and by username/password flows.
7. THE feature SHALL NOT alter `IdentityConstants.ApplicationScheme`, the JwtBearer scheme, IdentityServer signing keys, validation keys, scope definitions, or token lifetimes.

### Requirement 8: Edge cases và TTL handling

**User Story:** As a user, I want clear behaviour when the chooser session expires, the cookie is missing, or the candidate set has changed, so that I am not stuck on a broken page and I am safely redirected.

#### Acceptance Criteria

1. IF the user opens the Account_Select_Page tab in two browsers simultaneously and one tab consumes the Account_Select_Cookie via successful selection, THEN THE second tab's POST SHALL be rejected with Account_Select_Generic_Error (because the cookie no longer exists on the second tab's request, or because the SelectionToken protector key has been rotated due to cookie deletion — both branches collapse to the generic error).
2. IF `now_utc - Account_Select_Context.IssuedAtUtc > MultiAccount:SelectTtlSeconds`, THEN THE Phone_Login_Controller SHALL reject with Account_Select_Expired_Error AND clear the Account_Select_Cookie AND redirect to `/Account/Login` preserving `returnUrl`.
3. WHEN the user is redirected back to `/Account/Login` after Account_Select_Expired_Error, THE Login_Page SHALL render Generic_Error inside the "Số điện thoại" tab's validation area exactly as the existing R7.5 of `phone-otp-login` defines, with the localized text from Account_Select_Expired_Error.
4. IF `Account_Select_Context.CandidateUserIds` becomes empty (every candidate deleted between issuance and POST), THEN THE Phone_Login_Controller SHALL reject with Account_Select_Generic_Error AND redirect to `/Account/Login`.
5. IF a candidate is in `Account_Select_Context.CandidateUserIds` but has been deleted at POST time, THEN THE re-load step in Requirement 6.6(e) SHALL fail AND THE Phone_Login_Controller SHALL reject the specific selection with Account_Select_Generic_Error AND THE response SHALL re-render Account_Select_Page with the surviving candidates AND the Account_Select_Cookie SHALL remain valid so the user can pick a different candidate within the original TTL.
6. IF the user submits a `SelectionToken` that is well-formed but does not match any `userId` in `Account_Select_Context.CandidateUserIds` (tampering attempt), THEN THE Phone_Login_Controller SHALL reject with Account_Select_Generic_Error AND emit a Warning log `Event="PhoneOtpAccountSelectTokenInvalid"` AND increment the same per-phone failure counter referenced in Requirement 6.7.
7. IF the user opens `/Account/LoginWithPhone/SelectAccount` after the Identity cookie is already issued (e.g. browser back-button after a successful sign-in), THEN THE Phone_Login_Controller SHALL detect the absent Account_Select_Cookie AND redirect (HTTP 302) to `/Account/Login`, allowing the existing "already signed in" handling on `/Account/Login` to take over.
8. IF the back-link is clicked, THEN THE Phone_Login_Controller's GET handler for `/Account/Login` SHALL NOT need any special-case code — the Account_Select_Cookie SHALL be deleted by the SelectAccount controller branch only when the user navigates away within the same response (or by TTL expiry); a stale Account_Select_Cookie that survives a back-navigation SHALL be tolerated and re-evaluated on its next POST per Requirement 6.6.
9. THE Account_Select_Cookie SHALL be deleted on logout (i.e. when `AccountController.Logout` runs); this is achieved automatically by setting `Path = "/"` on the cookie AND verifying that no logout code path explicitly preserves cookies in this name pattern.

### Requirement 9: Multi-tenant scoping cho candidate set và account select

**User Story:** As a tenant administrator, I want the chooser to only ever offer accounts in the current tenant resolved from the host, so that cross-tenant identity leakage is impossible.

#### Acceptance Criteria

1. THE Phone_Otp_Service SHALL build the Candidate_Set with the SQL filter `u.TenantKey == request.TenantKey` (already enforced today for single-user lookup; this requirement reaffirms the constraint).
2. THE Account_Select_Context payload SHALL carry `TenantKey` AND each request to `/Account/LoginWithPhone/SelectAccount` SHALL re-resolve `ITenantContextAccessor.Current.TenantKey` AND reject any mismatch per Requirement 5.3 / 6.6(c).
3. WHEN re-loading a `UserIdentity` per Requirement 6.6(e), THE Phone_Login_Controller SHALL include `u.TenantKey == ITenantContextAccessor.Current.TenantKey` in the WHERE clause AND SHALL reject otherwise.
4. THE Account_Select_Cookie SHALL NOT be readable across subdomains; THE cookie domain SHALL be the one chosen by ASP.NET Core defaults (i.e. host-only) AND SHALL NOT be set with an explicit parent-domain value.

### Requirement 10: Logging và audit

**User Story:** As an operator, I want every step of the multi-account select flow to be logged with redacted PII, so that I can audit and triage abuse without leaking phone or user details.

#### Acceptance Criteria

1. WHEN a Candidate_Set with `Count > 1` is built, THE Phone_Otp_Service SHALL emit one Information-level log entry containing `Event="PhoneOtpRequest"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `Outcome="Issued"`, `CandidateCount` AND SHALL NOT log any individual user-id.
2. WHEN the Phone_Login_Controller redirects to `/Account/LoginWithPhone/SelectAccount` after verify success, THE controller SHALL emit one Information-level log entry containing `Event="PhoneOtpAccountSelectShown"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `CandidateCount`.
3. WHEN a user successfully selects an account, THE Phone_Login_Controller SHALL emit one Information-level log entry containing `Event="PhoneOtpAccountSelected"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `User_Id_Hash`, `Outcome="Succeeded"`.
4. WHEN any rejection branch in Requirement 6.6 fires, THE Phone_Login_Controller SHALL emit one Warning-level log entry with `Event="PhoneOtpAccountSelected"`, `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `User_Id_Hash` (when the userId was resolved before reject), `Outcome` ∈ `{ "Expired", "TenantMismatch", "TokenInvalid", "UserNotFound", "UserDisabled", "UserLockedOut" }`, AND SHALL NOT include the raw `UserIdentity.Id` or the cookie value.
5. THE Phone_Login_Controller SHALL NOT log full phone, full email, raw `UserIdentity.Id`, OTP plaintext, OTP_Hash, Account_Select_Cookie raw value, or SelectionToken raw value at any level.
6. THE Phone_Login_Controller SHALL emit a Warning-level log entry `Event="PhoneOtpAccountSelectExpired"` whenever Requirement 5.4 / 8.2 fires, with `TenantKey`, `Phone_Last4`, `Phone_Sha8`.

### Requirement 11: Rate limit, lockout, và brute-force resistance cho account select

**User Story:** As an STS operator, I want failures at the account-select step to count toward the same lockout window as failed OTP verifies, so that an attacker cannot bypass lockout by abusing the chooser endpoint.

#### Acceptance Criteria

1. THE Phone_Login_Controller SHALL invoke `Phone_Otp_Service.RegisterVerifyFailureAsync(tenantKey, phoneE164Hash, ct)` (or an equivalent per-phone counter API exposed by the service) for every rejection branch in Requirement 6.6 EXCEPT decrypt-failure — decrypt failures (cookie tampering / key rotation / cookie absent) SHALL be logged at Warning but SHALL NOT count toward the per-phone failure counter, because the controller cannot trust the phone identity from a tampered cookie.
2. WHEN the per-phone failure counter exceeds `PhoneVerifyLockoutMaxFailures` within `PhoneVerifyLockoutWindowSeconds`, THE Phone_Otp_Service SHALL reject all subsequent OTP issuance requests for that `phone_e164` until the lockout window expires (this is the existing R6.4 contract of `phone-otp-login` — this requirement reaffirms the controller MUST flow account-select failures into the same counter).
3. THE Phone_Login_Controller SHALL apply a per-IP rate-limit on POST `/Account/LoginWithPhone/SelectAccount` as defined in Requirement 18, in addition to the per-phone failure counter referenced in Requirement 11.1.
4. THE Phone_Login_Controller SHALL apply a randomized delay sampled uniformly from the interval `[100ms, 300ms]` to every Account_Select_Page POST rejection branch in Requirement 6.6 to mitigate timing side-channels between "userId not in set", "user disabled", and "user locked out".
5. THE Phone_Login_Controller SHALL NOT apply any randomized delay to the success branch.

### Requirement 12: Accessibility (WCAG 2.1 AA)

**User Story:** As a user with assistive-technology needs, I want the account chooser to be fully keyboard-navigable, screen-reader-friendly, and to meet WCAG AA, so that I can complete sign-in without a mouse.

#### Acceptance Criteria

1. THE Account_Select_Page SHALL render a single visible `<h1>` (the page title) AND a single `<h2>` or `<p>` subtitle, in document order before the chooser form, so that screen-reader users land in a meaningful heading on page load.
2. THE Account_Select_Page SHALL render a `<label>` element for the `<select>` dropdown with `for="account-select"` and localized text from the resource key `LoginWithPhone.SelectAccount.DropdownLabel`, so that screen readers announce the field's purpose when focus enters the dropdown.
3. THE `<select>` element SHALL carry `id="account-select"`, `name="SelectionToken"`, AND `aria-required="true"` so that assistive technology recognizes it as a required form field.
4. THE Account_Select_Page SHALL place initial keyboard focus on the `<select>` element via a single `autofocus` attribute on that element (no JS-driven focus management); the native `<select>` SHALL support arrow-key, Home/End, type-ahead, and Tab navigation by default browser behaviour.
5. THE submit button SHALL be a real `<button type="submit">` carrying an `aria-label` populated from the resource key `LoginWithPhone.SelectAccount.SubmitAriaLabel` (e.g. "Đăng nhập với tài khoản đã chọn" / "Sign in with the selected account") so that screen readers announce its purpose distinctly from the visible "Tiếp tục" / "Continue" label.
6. THE Account_Select_Page SHALL meet WCAG 2.1 contrast AA for all visible text (4.5:1 for body, 3:1 for ≥ 18pt) AND SHALL inherit the same theme tokens already used by `Phone_Verify_Page` (`btn-gradient-primary`, `link-secondary`, `text-muted-foreground`, etc.) so that no new contrast computation is needed.
7. THE Account_Select_Page SHALL NOT use `role="button"` on non-button elements; the only clickable affordance SHALL be the single `<button type="submit">` that submits the form.
8. THE Account_Select_Page error region SHALL be a `role="alert"` container, identical in attribute set to the existing alert region on `Phone_Verify_Page`, so that screen-readers announce the error on render.
9. WHERE a candidate has an empty `UserName` value at render time (which Identity normally disallows), THE corresponding `<option>` SHALL be omitted rather than rendered as an empty visible label, so that no empty entry appears in the dropdown.

### Requirement 13: Internationalization (vi default, en supported)

**User Story:** As an operator running both Vietnamese and English tenants, I want every visible string on the chooser page to be localizable through the existing `IViewLocalizer` pipeline, so that I can deploy in either language without code changes.

#### Acceptance Criteria

1. THE Account_Select_Page SHALL render every visible string via `IViewLocalizer` keys, NOT inline string literals.
2. THE feature SHALL define the following resource keys in both `vi` and `en` resx files under `Resources/Views/Account/LoginWithPhone/SelectAccount.{vi,en}.resx`: `LoginWithPhone.SelectAccount.Title`, `LoginWithPhone.SelectAccount.Subtitle`, `LoginWithPhone.SelectAccount.MaskedPhonePrefix`, `LoginWithPhone.SelectAccount.DropdownLabel`, `LoginWithPhone.SelectAccount.Continue`, `LoginWithPhone.SelectAccount.SubmitAriaLabel`, `LoginWithPhone.SelectAccount.ExpiredError`, `LoginWithPhone.SelectAccount.GenericError`, `LoginWithPhone.SelectAccount.BackToLogin`.
3. THE feature SHALL set the Vietnamese (vi) resource as the default culture AND English (en) as the secondary culture, mirroring the existing convention used by `Verify.{vi,en}.resx`.
4. THE Account_Select_Page SHALL render the masked-phone prefix using `LoginWithPhone.SelectAccount.MaskedPhonePrefix` followed by the masked phone string produced by `IPhoneNumberNormalizer.MaskLast4`, identical to the masking pattern on `Phone_Verify_Page`.
5. THE feature SHALL NOT introduce inline English text in the `.cshtml` view file even as a fallback.

### Requirement 14: Backward compatibility với case 1-user

**User Story:** As an existing user who is the only account on my phone number, I want my login experience to remain unchanged after this feature ships, so that I am never confused by an unnecessary account chooser.

#### Acceptance Criteria

1. WHEN the user lookup returns exactly one candidate, THE Phone_Login_Controller SHALL execute the existing single-user verify continuation (sign-in directly, no chooser shown).
2. WHEN the user lookup returns exactly one candidate, THE STS_Host SHALL NOT issue an Account_Select_Cookie on the response.
3. WHEN the user lookup returns exactly one candidate, THE response cookie set, headers, status code, and HTML on `/Account/LoginWithPhone/Verify` GET / POST SHALL be byte-for-byte identical to the response produced by the previous code path (modulo the natural variability of anti-forgery tokens and cookie values).
4. WHEN `MultiAccount:Enabled = false`, THE feature SHALL be invisible: GET `/Account/LoginWithPhone/SelectAccount` returns 404, no new cookies are set, no new log events are emitted, and the `OtpStoreRecord` serialization SHALL NOT include the `CandidateUserIds` field for newly-issued records (or SHALL include it as a single-element array equal to `[record.UserId]` to preserve forward compatibility — implementation choice MAY pick either, but the chosen behaviour MUST be tested per Requirement 16.7).

### Requirement 15: Coexistence với existing username/password và OIDC continuation

**User Story:** As a downstream OIDC client, I want the cookie issued by the multi-account flow to be byte-equivalent in scheme and properties to the cookie issued by the existing single-user phone-OTP flow and the username/password flow, so that no client-side changes are needed.

#### Acceptance Criteria

1. WHEN account-select sign-in succeeds, THE Identity cookie scheme name, expiration policy, and `SignInScheme` registration SHALL be unchanged from today.
2. WHEN account-select sign-in succeeds AND `returnUrl` is an IdentityServer authorization endpoint, THE Phone_Login_Controller SHALL hand off to the same `Redirect(returnUrl)` / `LoadingPage("Redirect", returnUrl)` branches that `AccountController.Login` and the existing `PhoneLoginController.Verify` action use (including the native-client branch).
3. THE feature SHALL NOT modify `IdentityServerOptions`, `ServerSideSessionsConfiguration`, or cookie expiration values.
4. THE feature SHALL NOT modify `AddEmailSenders` / `AddSmsSenders` registration ordering in `Startup.cs`.
5. THE feature SHALL NOT bypass `TenantResolutionMiddleware`.

### Requirement 16: Testing acceptance

**User Story:** As a developer, I want automated tests to cover the multi-account branch end-to-end without calling Twilio or a real database, so that regressions are caught in CI.

#### Acceptance Criteria

1. THE solution SHALL contain unit tests for `Phone_Otp_Service.IssueAsync` that exercise: `Count==0` rejection (unchanged), `Count==1` single-user path (unchanged), `Count>1 AND MultiAccount:Enabled=false` rejection (preserved legacy behaviour), `Count>1 AND MultiAccount:Enabled=true` issuance with Candidate_Set persisted, AND Candidate_Set ordering determinism.
2. THE solution SHALL contain unit tests for `OtpStoreRecord` serialization round-trip ensuring `Deserialize(Serialize(record)) == record` for both the new `CandidateUserIds` field and the legacy single-`UserId` shape (forward-/backward-compatible).
3. THE solution SHALL contain unit tests for `PhoneOtpAccountSelectCookieCodec` (or whatever class houses the protector) ensuring `Unprotect(Protect(payload)) == payload`, decrypt-failure on tampered ciphertext, and decrypt-failure on payload from a different protector purpose.
4. THE solution SHALL contain unit tests for the `SelectionToken` mapping ensuring: for any pair `(payload, userId in payload.CandidateUserIds)`, `MapToken(token) == userId`; for any tampered token of correct format, `MapToken(token)` returns null; for a token bound to a different `payload`, `MapToken(token)` returns null.
5. THE solution SHALL contain controller-level integration tests booting the STS_Host with `PhoneOtpLogin:Enabled = true`, `MultiAccount:Enabled = true`, Fake_Sms_Sender registered, and a seeded multi-tenant DB containing two `UserIdentity` rows sharing the same `(TenantKey, PhoneNumber)` — assertions: POST `/Account/LoginWithPhone/Request` with that phone returns HTTP 302 to `/Verify`; the subsequent GET `/Verify` returns markup byte-equivalent to the markup returned for a single-user phone (modulo anti-forgery and cooldown values); POST `/Verify` with the correct OTP returns HTTP 302 to `/Account/LoginWithPhone/SelectAccount?returnUrl=...` and sets `phone_otp_account_select` cookie; GET `/SelectAccount` renders a single `<form method="post">` containing one `<select id="account-select">` with one `<option>` per candidate in deterministic order (with the first option marked `selected`) and a single submit button; POST `/SelectAccount` with a valid `SelectionToken` returns HTTP 302 to `returnUrl` and issues the Identity cookie.
6. THE solution SHALL contain integration tests asserting anti-enumeration: the POST `/Request` and GET `/Verify` responses (status, headers, cookies, response body) for a phone matching exactly one user SHALL diff against those for a phone matching three users with no semantic difference (only anti-forgery tokens, cooldown values, and cookie values may differ).
7. THE solution SHALL contain integration tests asserting `/Account/LoginWithPhone/SelectAccount` returns HTTP 404 when `MultiAccount:Enabled = false` even if `PhoneOtpLogin:Enabled = true`.
8. THE solution SHALL contain integration tests asserting backward compatibility: when `MultiAccount:Enabled = false`, an `OtpStoreRecord` issued by the previous code (without `CandidateUserIds`) deserializes successfully and the verify continues to work.
9. THE solution SHALL contain integration tests asserting failure-counter integration: three POSTs to `/SelectAccount` with mutated `SelectionToken` SHALL increment the per-phone failure counter; once `PhoneVerifyLockoutMaxFailures` is reached, a fresh POST `/Account/LoginWithPhone/Request` for that phone SHALL be rejected with the same indistinguishable rejection as today's lockout case.
10. THE solution SHALL contain accessibility tests (DOM-assertion via integration test reading the rendered HTML) that the chooser markup includes a single `<h1>`, a single `<form method="post">` with a `<label for="account-select">`, a `<select id="account-select" aria-required="true" autofocus>` containing one `<option>` per surviving candidate (the first option `selected`), AND a single `<button type="submit">` carrying a non-empty `aria-label`.
11. THE test suite SHALL NOT make outbound network calls to Twilio in any configuration.
12. THE solution SHALL contain controller-level integration tests for the per-IP rate-limit defined in Requirement 18: with `IpSelectRateLimitMaxRequests` lowered to a small value, repeated POSTs to `/Account/LoginWithPhone/SelectAccount` from the same IP within `IpSelectRateLimitWindowSeconds` SHALL trigger the rate-limit branch (HTTP 429 or the configured 302-redirect equivalent), AND the Warning log entry `Event="PhoneOtpAccountSelectIpRateLimited"` SHALL be emitted with `IpHash` and `TenantKey` and SHALL NOT contain raw IP.

### Requirement 17: Non-functional và compatibility

**User Story:** As an STS operator, I want this feature to introduce no change to existing token, session, or auth-scheme behaviour, so that downstream clients and SSO continue to work without changes.

#### Acceptance Criteria

1. THE STS_Host SHALL preserve `IdentityConstants.ApplicationScheme`, `IdentityConstants.ExternalScheme`, and JwtBearer scheme registrations unchanged.
2. THE STS_Host SHALL preserve `IdentityServerOptions`, `ServerSideSessionsConfiguration`, and Identity-cookie expiration values unchanged.
3. THE STS_Host SHALL NOT introduce a new logical cache key prefix beyond what already exists; Account_Select_Context lives only in a cookie, not in Otp_Store, AND therefore SHALL NOT contend with the existing `"otp:"` key namespace.
4. THE STS_Host startup ordering SHALL NOT change as part of this feature; the new `MultiAccount` configuration SHALL be bound by the existing options pipeline that already binds `PhoneOtpLogin`.
5. THE feature SHALL NOT change DB schema; in particular, `UserIdentity.PhoneNumber` SHALL NOT be made unique within tenant by this feature, AND no migration is required.
6. THE feature SHALL NOT introduce a new NuGet package dependency.
7. THE feature SHALL NOT affect the username/password tab on `/Account/Login` AND SHALL NOT affect `Phone_Verify_Page` markup beyond the no-op compatibility constraints in Requirement 3.

### Requirement 18: Per-IP rate-limit cho POST /SelectAccount

**User Story:** As an STS operator, I want the account-select POST endpoint to enforce a per-IP rate-limit reusing the existing OTP rate-limit infrastructure, so that a single IP cannot brute-force `SelectionToken` values or exhaust the per-phone lockout window.

#### Acceptance Criteria

1. THE Phone_Login_Controller SHALL enforce a per-IP rate-limit on POST `/Account/LoginWithPhone/SelectAccount` using the existing `IPhoneOtpRateLimiter` infrastructure (`PhoneOtpRateLimiter`), reusing the same IP issuance/verify counter pattern; implementation MAY add a new method pair `RegisterIpSelectAttemptAsync(ipHash, ct)` and `CheckIpSelectAsync(ipHash, ct)` on `IPhoneOtpRateLimiter` if needed.
2. THE STS_Host SHALL bind the configuration keys `PhoneOtpLogin:MultiAccount:IpSelectRateLimitWindowSeconds` (default `600`, range `[60, 3600]`) AND `PhoneOtpLogin:MultiAccount:IpSelectRateLimitMaxRequests` (default `30`, range `[5, 200]`) AND SHALL fail-fast at startup if either value is outside its range (this is the same fail-fast contract reaffirmed by Requirement 1.7).
3. WHEN an IP exceeds `IpSelectRateLimitMaxRequests` within `IpSelectRateLimitWindowSeconds`, THE Phone_Login_Controller SHALL reject every subsequent POST `/Account/LoginWithPhone/SelectAccount` from that IP with the same convention used by the existing IP rate-limit on `/Request` (i.e. HTTP 429 if the existing convention is 429, otherwise an HTTP 302 redirect to `/Account/Login` carrying Account_Select_Generic_Error in TempData) so that the user-facing UX remains consistent across endpoints.
4. WHEN the IP rate-limit branch fires, THE Phone_Login_Controller SHALL emit a Warning-level log entry containing `Event="PhoneOtpAccountSelectIpRateLimited"`, `IpHash` (the first 8 hex characters of the SHA-256 hash of the client IP), `TenantKey`, `Outcome="RateLimited"` AND SHALL NOT log the raw client IP at any level.
5. THE Phone_Login_Controller SHALL increment the per-IP rate-limit counter on EVERY POST attempt to `/Account/LoginWithPhone/SelectAccount`, regardless of whether the selection ultimately succeeds or fails, so that a compromised account cannot serve as a free pass for high-volume bot traffic from a single IP.
6. THE Phone_Login_Controller SHALL evaluate the IP rate-limit BEFORE the cookie decryption step in Requirement 6.6(a), so that requests with a tampered or missing Account_Select_Cookie still consume IP budget.
7. WHEN the request is rejected by the IP rate-limit branch, THE Phone_Login_Controller SHALL still apply the randomized rejection delay defined in Requirement 11.4 to keep timing characteristics indistinguishable from the per-phone failure branches.

## Out of Scope

- SSO / external provider integration (Google, Microsoft, Twilio Verify) — not modelled here.
- Auto-provisioning new users by phone number — explicitly forbidden by `phone-otp-login` Requirement 11.1, unchanged.
- Admin UI tooling to merge / dedupe duplicate phone numbers — separate spec if needed.
- Email or push as alternative second factor — separate spec.
- Per-account "remember last selected" persistence — explicitly excluded.
- The login-page tab control AND password eye-toggle — already owned by `phone-otp-login` and `login-ui-redesign-i18n` specs.
- Schema-level uniqueness or normalization of `UserIdentity.PhoneNumber` — out of scope; the feature explicitly tolerates the existing legacy phone-format heuristic in `BuildPhoneLookupCandidates`.

## Acceptance Criteria Mapping

| Requirement | Acceptance Criteria | Maps To |
| --- | --- | --- |
| R1 Configuration flag | 1.1–1.8 | Operator-toggleable rollout, fail-fast on bad config |
| R2 IssueAsync supports many candidates | 2.1–2.8 | Multi-user matching, candidate ordering, cache compatibility |
| R3 Anti-enumeration on Verify | 3.1–3.7 | Verify page indistinguishable across `Count` |
| R4 Branching after verify | 4.1–4.6 | Single → sign-in; many → chooser |
| R5 Account_Select_Page rendering | 5.1–5.15 | Dropdown form factor, raw username text, deterministic order, fallbacks |
| R6 Cookie / token security | 6.1–6.12 | DataProtection signing, single-use, tampering rejection |
| R7 Sign-in continuation | 7.1–7.7 | Byte-equivalent sign-in, OIDC continuation |
| R8 Edge cases | 8.1–8.9 | TTL expiry, deleted candidate, double-submit |
| R9 Multi-tenant scoping | 9.1–9.4 | Candidate set + cookie + reload, all tenant-scoped |
| R10 Logging / audit | 10.1–10.6 | Structured events, redacted PII |
| R11 Lockout integration | 11.1–11.5 | Account-select failures count toward lockout, randomized delay |
| R12 Accessibility | 12.1–12.9 | WCAG 2.1 AA, dropdown labelling, native `<select>` keyboard support |
| R13 i18n | 13.1–13.5 | vi default, en supported, no inline strings |
| R14 Backward compatibility | 14.1–14.4 | Single-user UX unchanged, flag-off invisible |
| R15 Coexistence | 15.1–15.5 | OIDC clients see no change |
| R16 Testing | 16.1–16.12 | Unit + integration coverage, IP rate-limit coverage, no outbound calls |
| R17 Non-functional / compatibility | 17.1–17.7 | No scheme, schema, or dependency changes |
| R18 Per-IP rate-limit on /SelectAccount | 18.1–18.7 | Reuse `IPhoneOtpRateLimiter`, IP-hashed logging, evaluated before cookie decode |
