// Feature: tenant-client-cache-expansion, Task 5
//
// Internal helpers used by TenantClientCacheService to emit safe log
// messages and to surface the current correlation id without depending on
// IHttpContextAccessor (Task 5 keeps the service controller-agnostic).
//
// Validates: Requirements 13.2, 13.4, 13.6, 13.7

#nullable enable

using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

internal static class LogRedaction
{
    /// <summary>
    /// Hard ceiling on the redacted exception message. Keep this aligned
    /// with the requirement "≤ 256 characters" (R13.4).
    /// </summary>
    internal const int MaxMessageLength = 256;

    /// <summary>
    /// Redact substrings that look like connection-string credentials
    /// (<c>password=</c>, <c>auth=</c>, with or without a leading comma)
    /// and their value up to the next separator. Case-insensitive.
    /// </summary>
    private static readonly Regex CredentialPattern = new(
        @"(?i)(?:,\s*)?(password|auth)\s*=\s*[^,;\s]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Produce a log-safe representation of <paramref name="ex"/>'s message.
    /// </summary>
    /// <remarks>
    /// The returned string is suitable to inject into a structured-log
    /// template field (e.g. <c>{ExceptionMessage}</c>). Callers MUST NOT
    /// pass <c>ex.ToString()</c> through this helper — that would include
    /// stack traces, which we never log per R13.4.
    /// </remarks>
    public static string SanitizeExceptionMessage(Exception? ex)
    {
        if (ex is null)
        {
            return string.Empty;
        }

        var message = ex.Message ?? string.Empty;
        var redacted = CredentialPattern.Replace(message, "***");

        if (redacted.Length > MaxMessageLength)
        {
            redacted = redacted.Substring(0, MaxMessageLength);
        }

        return redacted;
    }

    /// <summary>
    /// Best-effort correlation id pulled from the ambient
    /// <see cref="Activity.Current"/>. Returns <c>null</c> when no activity
    /// is in flight (e.g. background sweep before the host attaches a
    /// listener). Per R13.2 callers must accept <c>null</c>.
    /// </summary>
    public static string? CurrentCorrelationId()
        => Activity.Current?.TraceId.ToString();
}
