using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Models;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Storage;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed class PhoneOtpService : IPhoneOtpService
{
    private readonly IPhoneOtpStore _store;
    private readonly IPhoneOtpRateLimiter _rateLimiter;
    private readonly IPhoneNumberNormalizer _normalizer;
    private readonly ISmsSender _smsSender;
    private readonly UserManager<UserIdentity> _userManager;
    private readonly ILogger<PhoneOtpService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly PhoneOtpLoginConfiguration _config;
    private readonly byte[] _hmacKey;

    public PhoneOtpService(
        IPhoneOtpStore store,
        IPhoneOtpRateLimiter rateLimiter,
        IPhoneNumberNormalizer normalizer,
        ISmsSender smsSender,
        UserManager<UserIdentity> userManager,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<PhoneOtpLoginConfiguration> options,
        ILogger<PhoneOtpService> logger,
        TimeProvider timeProvider)
    {
        _store = store;
        _rateLimiter = rateLimiter;
        _normalizer = normalizer;
        _smsSender = smsSender;
        _userManager = userManager;
        _logger = logger;
        _timeProvider = timeProvider;
        _config = options.Value;

        var protector = dataProtectionProvider.CreateProtector("PhoneOtp.HashKey");
        _hmacKey = protector.Protect(Encoding.UTF8.GetBytes("phone-otp-hash-v1"));
    }

    public async Task<IssueOtpResult> IssueAsync(IssueOtpRequest request, CancellationToken ct)
    {
        // 1. Normalize phone number
        if (!_normalizer.TryNormalize(request.RawPhone, _config.DefaultRegion, out var e164))
        {
            _logger.LogInformation("PhoneOtpRequest: normalize failed for input. {Event} {TenantKey} {Outcome}",
                "PhoneOtpRequest", request.TenantKey, "Rejected");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        var phoneHash = Sha256Hex(e164);
        var ipHash = Sha256Hex(request.RemoteIp);

        // 2. Check IP rate limit
        var ipCheck = await _rateLimiter.CheckIpAsync(ipHash, ct);
        if (!ipCheck.Allowed)
        {
            _logger.LogWarning("PhoneOtpRequest: IP rate limit hit. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpRequest", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "IpWindow");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        // 3. Check phone cooldown
        var cooldownCheck = await _rateLimiter.CheckPhoneCooldownAsync(request.TenantKey, phoneHash, ct);
        if (!cooldownCheck.Allowed)
        {
            _logger.LogWarning("PhoneOtpRequest: phone cooldown active. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpRequest", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "PhoneCooldown");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, cooldownCheck.CooldownRemainingSeconds);
        }

        // 4. Check phone lockout
        var lockoutCheck = await _rateLimiter.CheckPhoneLockoutAsync(request.TenantKey, phoneHash, ct);
        if (!lockoutCheck.Allowed)
        {
            _logger.LogWarning("PhoneOtpRequest: phone lockout active. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpRequest", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "PhoneLockout");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        // 5. Lookup user — match by E.164 first (canonical post-onboarding), fall
        //    back to legacy national-format candidates so users whose phone was
        //    saved before the OTP feature went live can still log in.
        //    Each candidate is tried with PhoneNumberConfirmed=true and the
        //    request's tenant scope to keep the security boundary intact.
        var phoneCandidates = BuildPhoneLookupCandidates(e164, request.RawPhone);
        var users = await _userManager.Users
            .Where(u => phoneCandidates.Contains(u.PhoneNumber)
                        && u.PhoneNumberConfirmed
                        && u.TenantKey == request.TenantKey)
            .ToListAsync(ct);

        if (users.Count != 1)
        {
            _logger.LogInformation("PhoneOtpRequest: user lookup failed (count={UserCount}). {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome}",
                users.Count, "PhoneOtpRequest", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        var user = users[0];

        // 6. Generate OTP
        var otp = GenerateOtp(_config.OtpLength);

        // 7. Compute HMAC hash
        var hash = ComputeHash(otp);

        // 8. Build OTP store record
        var now = _timeProvider.GetUtcNow();
        var record = new OtpStoreRecord
        {
            OtpHash = hash,
            TenantKey = request.TenantKey,
            PhoneE164 = e164,
            UserId = user.Id,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(_config.OtpTtlSeconds),
            AttemptCount = 0
        };

        // 9. Store record
        await _store.SetAsync(request.TenantKey, phoneHash, record, TimeSpan.FromSeconds(_config.OtpTtlSeconds), ct);

        // 10. Register rate limit counters
        await _rateLimiter.RegisterPhoneIssuanceAsync(request.TenantKey, phoneHash, ct);
        await _rateLimiter.RegisterIpIssuanceAsync(ipHash, ct);

        // 11. Send SMS
        var smsBody = $"Mã đăng nhập của bạn: {otp}. Mã có hiệu lực trong {_config.OtpTtlSeconds / 60} phút.";
        var smsResult = await _smsSender.SendAsync(e164, smsBody, ct);

        if (!smsResult.Succeeded)
        {
            _logger.LogError("PhoneOtpSmsSend: SMS delivery failed. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {ProviderErrorCode}",
                "PhoneOtpSmsSend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Failed", smsResult.ErrorCode);
            await _store.DeleteAsync(request.TenantKey, phoneHash, ct);
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        _logger.LogInformation("PhoneOtpRequest: OTP issued successfully. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome}",
            "PhoneOtpRequest", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Issued");

        return new IssueOtpResult(IssueOutcome.Issued, phoneHash, record.ExpiresAtUtc, null);
    }

    public async Task<VerifyOtpResult> VerifyAsync(VerifyOtpRequest request, CancellationToken ct)
    {
        // 1. Increment attempt count atomically
        var attemptCount = await _store.IncrementAttemptAsync(request.TenantKey, request.PhoneE164Hash, ct);

        if (attemptCount == 0)
        {
            _logger.LogInformation("PhoneOtpVerify: no session found. {Event} {TenantKey} {PhoneSha8} {RemoteIp} {Outcome}",
                "PhoneOtpVerify", request.TenantKey, request.PhoneE164Hash[..Math.Min(8, request.PhoneE164Hash.Length)], request.RemoteIp, "NoSession");
            return new VerifyOtpResult(VerifyOutcome.NoSession, null, 0);
        }

        // 2. Get record
        var record = await _store.GetAsync(request.TenantKey, request.PhoneE164Hash, ct);

        // 3. Check expiration
        if (record is null || _timeProvider.GetUtcNow() > record.ExpiresAtUtc)
        {
            _logger.LogInformation("PhoneOtpVerify: OTP expired. {Event} {TenantKey} {PhoneSha8} {RemoteIp} {Outcome} {AttemptCount}",
                "PhoneOtpVerify", request.TenantKey, request.PhoneE164Hash[..Math.Min(8, request.PhoneE164Hash.Length)], request.RemoteIp, "Expired", attemptCount);
            await _store.DeleteAsync(request.TenantKey, request.PhoneE164Hash, ct);
            return new VerifyOtpResult(VerifyOutcome.Expired, null, attemptCount);
        }

        // 4. Check exhaustion
        if (attemptCount > _config.MaxVerifyAttemptsPerOtp)
        {
            _logger.LogWarning("PhoneOtpVerify: max attempts exhausted. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {AttemptCount}",
                "PhoneOtpVerify", request.TenantKey, GetPhoneLast4(record.PhoneE164), request.PhoneE164Hash[..Math.Min(8, request.PhoneE164Hash.Length)], request.RemoteIp, "Exhausted", attemptCount);
            await _store.DeleteAsync(request.TenantKey, request.PhoneE164Hash, ct);
            await _rateLimiter.RegisterVerifyFailureAsync(request.TenantKey, request.PhoneE164Hash, ct);
            return new VerifyOtpResult(VerifyOutcome.Exhausted, null, attemptCount);
        }

        // 5. Compute hash of submitted OTP and compare
        var submittedHash = ComputeHash(request.SubmittedOtp);

        if (!CryptographicOperations.FixedTimeEquals(submittedHash, record.OtpHash))
        {
            _logger.LogInformation("PhoneOtpVerify: OTP mismatch. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {AttemptCount}",
                "PhoneOtpVerify", request.TenantKey, GetPhoneLast4(record.PhoneE164), request.PhoneE164Hash[..Math.Min(8, request.PhoneE164Hash.Length)], request.RemoteIp, "Mismatch", attemptCount);
            await _rateLimiter.RegisterVerifyFailureAsync(request.TenantKey, request.PhoneE164Hash, ct);
            return new VerifyOtpResult(VerifyOutcome.Mismatch, null, attemptCount);
        }

        // 6. Match! Delete record and return success
        _logger.LogInformation("PhoneOtpVerify: OTP verified successfully. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {AttemptCount}",
            "PhoneOtpVerify", request.TenantKey, GetPhoneLast4(record.PhoneE164), request.PhoneE164Hash[..Math.Min(8, request.PhoneE164Hash.Length)], request.RemoteIp, "Succeeded", attemptCount);
        await _store.DeleteAsync(request.TenantKey, request.PhoneE164Hash, ct);
        return new VerifyOtpResult(VerifyOutcome.Succeeded, record.UserId, attemptCount);
    }

    public async Task<IssueOtpResult> ResendAsync(IssueOtpRequest request, CancellationToken ct)
    {
        // ResendAsync: similar to IssueAsync but skip user lookup (user already bound in session cookie)
        // The controller passes the E164 phone directly in RawPhone field for resend

        // 1. Normalize phone (or use directly if already E164)
        string e164;
        if (!_normalizer.TryNormalize(request.RawPhone, _config.DefaultRegion, out e164))
        {
            // If RawPhone is already E164 format, try using it directly
            e164 = request.RawPhone;
        }

        var phoneHash = Sha256Hex(e164);
        var ipHash = Sha256Hex(request.RemoteIp);

        // 2. Check phone cooldown
        var cooldownCheck = await _rateLimiter.CheckPhoneCooldownAsync(request.TenantKey, phoneHash, ct);
        if (!cooldownCheck.Allowed)
        {
            _logger.LogInformation("PhoneOtpResend: cooldown active. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpResend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "PhoneCooldown");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, cooldownCheck.CooldownRemainingSeconds);
        }

        // 3. Check phone lockout
        var lockoutCheck = await _rateLimiter.CheckPhoneLockoutAsync(request.TenantKey, phoneHash, ct);
        if (!lockoutCheck.Allowed)
        {
            _logger.LogWarning("PhoneOtpResend: phone lockout active. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpResend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "PhoneLockout");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        // 4. Check IP rate limit
        var ipCheck = await _rateLimiter.CheckIpAsync(ipHash, ct);
        if (!ipCheck.Allowed)
        {
            _logger.LogWarning("PhoneOtpResend: IP rate limit hit. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {RateLimitReason}",
                "PhoneOtpResend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Rejected", "IpWindow");
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        // 5. Get existing record to retrieve UserId (user already bound in session)
        var existingRecord = await _store.GetAsync(request.TenantKey, phoneHash, ct);
        var userId = existingRecord?.UserId ?? string.Empty;

        // 6. Generate new OTP
        var otp = GenerateOtp(_config.OtpLength);

        // 7. Compute HMAC hash
        var hash = ComputeHash(otp);

        // 8. Build new record with AttemptCount = 0
        var now = _timeProvider.GetUtcNow();
        var record = new OtpStoreRecord
        {
            OtpHash = hash,
            TenantKey = request.TenantKey,
            PhoneE164 = e164,
            UserId = userId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(_config.OtpTtlSeconds),
            AttemptCount = 0
        };

        // 9. Store record (overwrites existing)
        await _store.SetAsync(request.TenantKey, phoneHash, record, TimeSpan.FromSeconds(_config.OtpTtlSeconds), ct);

        // 10. Register rate limit counters
        await _rateLimiter.RegisterPhoneIssuanceAsync(request.TenantKey, phoneHash, ct);
        await _rateLimiter.RegisterIpIssuanceAsync(ipHash, ct);

        // 11. Send SMS
        var smsBody = $"Mã đăng nhập của bạn: {otp}. Mã có hiệu lực trong {_config.OtpTtlSeconds / 60} phút.";
        var smsResult = await _smsSender.SendAsync(e164, smsBody, ct);

        if (!smsResult.Succeeded)
        {
            _logger.LogError("PhoneOtpSmsSend: SMS delivery failed on resend. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome} {ProviderErrorCode}",
                "PhoneOtpSmsSend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Failed", smsResult.ErrorCode);
            await _store.DeleteAsync(request.TenantKey, phoneHash, ct);
            return new IssueOtpResult(IssueOutcome.Rejected, null, null, null);
        }

        _logger.LogInformation("PhoneOtpResend: OTP resent successfully. {Event} {TenantKey} {PhoneLast4} {PhoneSha8} {RemoteIp} {Outcome}",
            "PhoneOtpResend", request.TenantKey, GetPhoneLast4(e164), phoneHash[..8], request.RemoteIp, "Issued");

        return new IssueOtpResult(IssueOutcome.Issued, phoneHash, record.ExpiresAtUtc, null);
    }

    #region Private Helpers

    private byte[] ComputeHash(string otp)
    {
        using var hmac = new HMACSHA256(_hmacKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(otp));
    }

    private static string Sha256Hex(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes);
    }

    private static string GenerateOtp(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }
        return new string(chars);
    }

    private static string GetPhoneLast4(string e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length < 4)
            return e164 ?? string.Empty;
        return e164[^4..];
    }

    /// <summary>
    /// Builds the set of phone-number string variants the user-lookup query should
    /// try in order to find a matching <c>AspNetUsers.PhoneNumber</c> row.
    /// <para>
    /// Canonical storage is E.164 (e.g. <c>+84334336232</c>), but pre-OTP onboarding
    /// flows historically wrote the phone in national format (e.g. <c>0334336232</c>)
    /// or with a stray space / dash. To keep the OTP feature usable against legacy
    /// data without a one-shot migration, we try several deterministic variants
    /// derived from the same parsed E.164:
    /// </para>
    /// <list type="bullet">
    /// <item><c>+84334336232</c> — canonical E.164 (always first).</item>
    /// <item><c>84334336232</c> — E.164 minus the leading <c>+</c>.</item>
    /// <item><c>0334336232</c> — VN-style national format (trunk <c>0</c> + subscriber digits)
    /// when the dial code is <c>+84</c>. Other regions get the same trunk-prefix
    /// approximation if they happen to use <c>0</c>; otherwise we skip this variant.</item>
    /// <item>The original raw input the user typed, trimmed.</item>
    /// </list>
    /// All variants are deduped (case-insensitive) and any whitespace / control
    /// characters in the raw value are stripped to avoid empty-string matches.
    /// </summary>
    private static IReadOnlyList<string> BuildPhoneLookupCandidates(string e164, string rawInput)
    {
        var candidates = new List<string>(capacity: 4);

        if (!string.IsNullOrEmpty(e164))
        {
            candidates.Add(e164);

            // Some legacy seeders dropped the leading '+'.
            if (e164.StartsWith('+') && e164.Length > 1)
            {
                candidates.Add(e164[1..]);
            }

            // VN-style national format with trunk-zero. Only emit when the country
            // code is +84 (the only region we know uses '0' trunk + 9 subscriber
            // digits). Generalising to every region would require libphonenumber's
            // PhoneNumberUtil.Format(NATIONAL) which we could plug in later via the
            // normalizer — keep this heuristic minimal for now.
            if (e164.StartsWith("+84", StringComparison.Ordinal) && e164.Length > 3)
            {
                candidates.Add("0" + e164[3..]);
            }
        }

        if (!string.IsNullOrWhiteSpace(rawInput))
        {
            // Strip every whitespace / control char but keep the user's original
            // shape (with or without '+' / '0' prefix) so a perfect-match row in
            // legacy data lights up even if our normalization disagreed.
            var trimmed = new string(rawInput.Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
            if (!string.IsNullOrEmpty(trimmed))
            {
                candidates.Add(trimmed);
            }
        }

        // Dedup (preserve first-seen order, case-insensitive — phone numbers are
        // numeric so the comparer mostly matters for the '+' character).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<string>(candidates.Count);
        foreach (var c in candidates)
        {
            if (seen.Add(c))
            {
                deduped.Add(c);
            }
        }
        return deduped;
    }

    #endregion
}
