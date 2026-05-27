# Phone OTP Multi-Account Select — Operator Guide

Hướng dẫn vận hành cho feature **Phone OTP Multi-Account Select** trong STS host (`Skoruba.Duende.IdentityServer.STS.Identity`). Feature này nới luồng đăng nhập phone-OTP để hỗ trợ trường hợp **một số điện thoại được gắn với nhiều `UserIdentity` cùng tenant**: sau khi user verify OTP thành công, nếu có nhiều candidate user thì hiển thị trang chọn account; nếu chỉ có 1 candidate thì sign-in thẳng (giữ nguyên UX).

Spec tham chiếu: `.kiro/specs/phone-otp-multi-account-select/{requirements,design,tasks}.md`.

---

## 1. Feature flag overview

| Flag | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `PhoneOtpLogin:Enabled` | `false` | Master switch của toàn bộ luồng phone-OTP login (spec gốc `phone-otp-login`). |
| `PhoneOtpLogin:MultiAccount:Enabled` | `false` | **Sub-flag của feature này.** Cho phép user login khi 1 số điện thoại gắn với nhiều account trong cùng tenant. |

**Ngữ nghĩa khi `PhoneOtpLogin:MultiAccount:Enabled = false`** (default, backward-compatible):

- `PhoneOtpService.IssueAsync` reject mọi nhánh `users.Count > 1` (đúng hành vi pre-existing của spec `phone-otp-login`).
- Route `GET/POST /Account/LoginWithPhone/SelectAccount` trả HTTP 404.
- Cookie `phone_otp_account_select` không được issue.
- Verify-page (`/Account/LoginWithPhone/Verify`) markup byte-for-byte không đổi.

**Ngữ nghĩa khi `PhoneOtpLogin:MultiAccount:Enabled = true`**:

- `PhoneOtpService.IssueAsync` accept `users.Count >= 1` (zero vẫn reject để giữ anti-enumeration).
- Sau verify OTP thành công với `CandidateUserIds.Count > 1`, controller redirect tới `/Account/LoginWithPhone/SelectAccount`.
- Trang chooser hiển thị HTML `<select>` chứa raw `UserIdentity.UserName` của từng candidate (không mask).
- `users.Count == 1` vẫn giữ nguyên UX cũ (verify → sign-in → returnUrl).

> **Lưu ý**: nếu `PhoneOtpLogin:Enabled = false` thì giá trị `MultiAccount:Enabled` bị bỏ qua hoàn toàn. STS host fail-fast tại startup nếu cấu hình `MultiAccount:Enabled = true` mà parent flag off.

---

## 2. Cách bật trong dev/staging/prod

### 2.1 Dev (local development)

File `src/Skoruba.Duende.IdentityServer.STS.Identity/appsettings.Development.json` đã được set sẵn `PhoneOtpLogin:MultiAccount:Enabled = true` từ Task 13. Không cần thao tác thêm khi chạy local.

Nếu muốn override không qua file (vd. test rollback path), dùng user-secrets trong project STS host:

```bash
cd src/Skoruba.Duende.IdentityServer.STS.Identity
dotnet user-secrets set "PhoneOtpLogin:MultiAccount:Enabled" "true"
# hoặc tắt:
dotnet user-secrets set "PhoneOtpLogin:MultiAccount:Enabled" "false"
```

### 2.2 Staging / Production

Production không bao giờ commit flag bật vào git. Bật bằng một trong hai cách:

- **Environment variable** (recommended, simplest): trên máy chạy STS host:

  ```bash
  export PhoneOtpLogin__Enabled=true
  export PhoneOtpLogin__MultiAccount__Enabled=true
  ```

  Lưu ý: ASP.NET Core mapping `:` trong config key sang `__` trong env var name.

- **Azure App Configuration / KeyVault / external config provider**: thêm key `PhoneOtpLogin:MultiAccount:Enabled` với giá trị `true`.

Sau khi đổi, **restart STS host process** để startup validation chạy lại (xem Section 3 để biết các range hợp lệ).

### 2.3 Disable nhanh (kill-switch)

Đặt `PhoneOtpLogin:MultiAccount:Enabled = false` rồi restart STS host. Behaviour quay về single-user mode (xem Section 7 — Rollback).

---

## 3. Configuration keys

Toàn bộ sub-section nằm trong `PhoneOtpLogin:MultiAccount` của `appsettings.json`:

| Key | Default | Range hợp lệ | Mô tả |
| --- | --- | --- | --- |
| `Enabled` | `false` | `bool` | Master switch của feature multi-account. |
| `SelectTtlSeconds` | `60` | `[30, 180]` | Thời gian sống của cookie `phone_otp_account_select` kể từ verify OTP thành công. |
| `IpSelectRateLimitWindowSeconds` | `600` | `[60, 3600]` | Cửa sổ rolling cho per-IP counter trên POST `/SelectAccount`. |
| `IpSelectRateLimitMaxRequests` | `30` | `[5, 200]` | Ngưỡng tối đa request POST `/SelectAccount` per-IP per window. Vượt ngưỡng → reject `Account_Select_Generic_Error`. |

**Fail-fast tại startup**: STS host throw `InvalidOperationException` (nêu đúng tên config key) nếu giá trị nằm ngoài range. Không có hành vi "best-effort clamp". Operator phải sửa config trước khi process khởi động.

Ví dụ JSON snippet đầy đủ trong `appsettings.json`:

```json
{
  "PhoneOtpLogin": {
    "Enabled": false,
    "MultiAccount": {
      "Enabled": false,
      "SelectTtlSeconds": 60,
      "IpSelectRateLimitWindowSeconds": 600,
      "IpSelectRateLimitMaxRequests": 30
    }
  }
}
```

---

## 4. Risk notes

### 4.1 False-positive lockout từ multi-account fail

Mỗi rejection tại POST `/SelectAccount` (gate 5..9 — tenant mismatch, token invalid, user not found, lockout, …) tăng counter `PhoneVerifyLockoutMaxFailures` (mặc định `10` trong `PhoneVerifyLockoutWindowSeconds = 3600`). Nếu user multi-account thật fail nhiều lần (chọn nhầm account, vô tình refresh, account bị disable giữa flow), counter có thể chạm threshold → user bị **lockout toàn bộ phone** (không issue OTP mới được trong window).

**Mitigation**:

- Threshold mặc định `10` đã khá cao cho usage pattern thực tế. Theo dõi metric `PhoneOtpAccountSelected Outcome != Succeeded` rate per tenant.
- Nếu false-positive xảy ra thường xuyên, bump `PhoneOtpLogin:Lockout:MaxFailures` (hoặc tên key tương đương đã có trong spec gốc) lên `15`/`20`.
- UI chooser hiển thị raw `UserName` để user dễ chọn đúng — KHÔNG hiển thị account locked-out vì đó là leak per-account state.

### 4.2 IP rate-limit bypass qua reverse proxy

Per-IP counter dùng `HttpContext.Connection.RemoteIpAddress`. Nếu STS host chạy sau reverse proxy (Nginx, Cloudflare, Azure Front Door, …) mà proxy KHÔNG forward `X-Forwarded-For` đúng, mọi request sẽ map về cùng IP loopback (`127.0.0.1`) hoặc IP của proxy → một user fail nhiều có thể **block toàn bộ fleet** đến khi window expire.

**Mitigation**:

- Kiểm tra `ForwardedHeadersOptions` đã được register trong startup pipeline (`UseForwardedHeaders`).
- Đảm bảo proxy gửi `X-Forwarded-For` và STS host trust được proxy network range.
- Test bằng cách POST `/SelectAccount` từ 2 client thật khác IP — log `PhoneOtpAccountSelectIpRateLimited` không được trigger ở client thứ hai khi client thứ nhất chưa vượt threshold.

### 4.3 Cookie hai cookie không bao giờ coexist

`phone_otp_session` (step-1 cookie) và `phone_otp_account_select` (step-2 cookie) có scheme codec/cookie khác nhau. Trước khi set cookie thứ hai, controller phải xoá cookie thứ nhất (đã implement). Nếu operator nhìn thấy hai cookie cùng xuất hiện trong response của cùng 1 request thì đó là regression — open issue ngay.

---

## 5. Telemetry / log events

Toàn bộ event được emit qua Serilog với structured properties bắt buộc: `TenantKey`, `Phone_Last4`, `Phone_Sha8`, optional `User_Id_Hash`, `Outcome`. **KHÔNG bao giờ log raw IP, raw `UserIdentity.Id`, raw cookie value, raw `SelectionToken`, hoặc OTP plaintext.**

| Event | Level | Khi nào fire | Properties (tóm tắt) |
| --- | --- | --- | --- |
| `PhoneOtpAccountSelectShown` | Info | User được redirect tới trang chooser sau verify OTP thành công với `CandidateUserIds.Count > 1`. | `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `CandidateCount` (numeric, audit-only). |
| `PhoneOtpAccountSelected` | Info (`Outcome="Succeeded"`) hoặc Warning (`Outcome ∈ {"TenantMismatch", "TokenInvalid", "UserNotFound", "UserDisabled", "UserLockedOut"}`) | Mỗi nhánh outcome của POST `/SelectAccount` handler. | `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `User_Id_Hash` (8 hex SHA-256, success branch), `Outcome`. |
| `PhoneOtpAccountSelectExpired` | Warning | `now > Account_Select_Context.ExpiresAtUtc` tại GET hoặc POST. | `TenantKey`, `Phone_Last4`, `Phone_Sha8`. |
| `PhoneOtpAccountSelectTokenInvalid` | Warning | `SelectionToken` decrypt fail HOẶC `userId` không nằm trong `Candidate_Set`. | `TenantKey`, `Phone_Last4`, `Phone_Sha8`, `Reason ∈ {"DecryptFail", "UserIdNotInSet"}`. |
| `PhoneOtpAccountSelectIpRateLimited` | Warning | Per-IP counter vượt `IpSelectRateLimitMaxRequests` trong window. | `TenantKey` (nếu resolve được), `Phone_Last4`/`Phone_Sha8` (nếu cookie hợp lệ trước rate-limit, có thể vắng), IP hash đã truncate. |

Operator dashboards (tham khảo):

- `rate(PhoneOtpAccountSelectShown[5m])` per tenant — baseline cho user-flow volume.
- `sum by(Outcome) (rate(PhoneOtpAccountSelected[5m]))` — tỷ lệ Success vs reject branches.
- `rate(PhoneOtpAccountSelectIpRateLimited[5m])` — alarm khi spike → có brute-force hoặc proxy mis-config (Section 4.2).
- `rate(PhoneOtpAccountSelectExpired[5m])` — nếu cao bất thường, kiểm tra latency frontend (user mất quá lâu giữa verify và chọn account) hoặc cân nhắc bump `SelectTtlSeconds` (max 180s).

---

## 6. Rollout checklist

### 6.1 Dev — smoke test

1. Set `PhoneOtpLogin:MultiAccount:Enabled = true` (đã sẵn trong `appsettings.Development.json`).
2. Seed DB với 2 user trong cùng tenant chia sẻ phone `+84334336232` (cả hai có `PhoneNumberConfirmed = true`).
3. Smoke flow:
   - POST `/Account/LoginWithPhone/Request` với phone trên → expect 302 `/Verify`, cookie `phone_otp_session` được set.
   - POST `/Account/LoginWithPhone/Verify` với OTP đúng → expect 302 `/Account/LoginWithPhone/SelectAccount?returnUrl=...`, cookie `phone_otp_account_select` được set, `phone_otp_session` đã clear.
   - GET `/Account/LoginWithPhone/SelectAccount` → render trang có đúng 2 `<option>`, cái đầu được mark `selected`.
   - POST `/Account/LoginWithPhone/SelectAccount` với `SelectionToken` của option 1 → expect 302 `returnUrl`, Identity cookie issued, `phone_otp_account_select` đã clear.
4. Test single-user backward-compat: phone chỉ match 1 user → expect verify trực tiếp → 302 `returnUrl`, KHÔNG đi qua chooser.
5. Test flag-off: tạm `Enabled=false` → POST `/SelectAccount` phải trả 404; phone match 2 user phải reject tại `IssueAsync`.

### 6.2 Staging — 1 tuần observability

1. Bật `PhoneOtpLogin:MultiAccount:Enabled = true` qua env var.
2. Restart STS host, verify startup log không có exception.
3. Monitor 1 tuần:
   - `PhoneOtpAccountSelectShown` rate per tenant — confirm có user thực sự rơi vào multi-account branch.
   - Tỷ lệ `PhoneOtpAccountSelected Outcome="Succeeded"` vs các reject Outcome — kỳ vọng > 90% Succeeded sau lần thử đầu tiên (user pick option đúng).
   - `PhoneOtpAccountSelectIpRateLimited` — phải gần 0 trong traffic bình thường; spike = brute-force attempt hoặc proxy config issue.
   - Lockout rate so với baseline pre-rollout — không tăng đột biến.
4. Nếu metric clean trong 7 ngày liên tục → bật prod.

### 6.3 Production

1. Bật flag qua env var hoặc Azure App Configuration.
2. Rolling restart STS host (không downtime nếu chạy multiple instance).
3. Theo dõi 24h đầu tiên: tỷ lệ Succeeded, không có spike `PhoneOtpAccountSelectIpRateLimited`, không có spike lockout.
4. Nếu có incident → rollback theo Section 7.

---

## 7. Rollback

Đơn giản, không cần migration:

1. Đặt `PhoneOtpLogin:MultiAccount:Enabled = false` trong env var hoặc config provider.
2. Rolling restart STS host.

Behaviour sau rollback:

- `IssueAsync` quay về reject `users.Count > 1` (single-user only).
- Route `GET/POST /SelectAccount` trả 404.
- Cookie `phone_otp_account_select` mới không được issue.
- **In-flight `phone_otp_session` cookies** (đã issue trước rollback) tiếp tục verify được — backward-compat của `OtpStoreRecord` (R2.6) đảm bảo record cũ deserialize OK với `CandidateUserIds = [record.UserId]`.
- **In-flight `phone_otp_account_select` cookies** sẽ tự expire trong ≤ 60 giây (TTL ≤ `SelectTtlSeconds`). Không cần invalidate cookie thủ công, không cần clear Redis key, không cần bump cookie codec key.

KHÔNG cần migration EF, KHÔNG cần restart Redis, KHÔNG cần force user logout.

---

## References

- Spec: `.kiro/specs/phone-otp-multi-account-select/requirements.md`
- Design: `.kiro/specs/phone-otp-multi-account-select/design.md`
- Task plan: `.kiro/specs/phone-otp-multi-account-select/tasks.md`
- Spec gốc (single-user phone-OTP): `.kiro/specs/phone-otp-login/`
