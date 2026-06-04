using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Services;

public sealed class ExternalPhoneOtpClient : IExternalPhoneOtpClient
{
    private static readonly Regex LongDigitRunRegex = new(@"\d{6,}", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PhoneOtpUserApiConfiguration _configuration;
    private readonly ILogger<ExternalPhoneOtpClient> _logger;

    public ExternalPhoneOtpClient(
        IHttpClientFactory httpClientFactory,
        IOptions<PhoneOtpLoginConfiguration> options,
        ILogger<ExternalPhoneOtpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = options.Value.UserApi;
        _logger = logger;
    }

    public async Task<ExternalPhoneOtpIssueResult> RequestAsync(
        ExternalPhoneOtpIssueRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            _configuration.RequestPath,
            new ExternalPhoneOtpRequestPayload(
                request.TenantKey,
                request.PhoneNumber,
                request.ClientId),
            cancellationToken).ConfigureAwait(false);

        var responseBody = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var body = TryParseBody(responseBody);
        LogResponse(_configuration.RequestPath, response, responseBody);

        var rejectionReason = ReadString(body, "rejectionReason", "reason", "error_description", "errorDescription", "error", "message");
        if (!response.IsSuccessStatusCode)
        {
            return new ExternalPhoneOtpIssueResult(
                Succeeded: false,
                MaskedPhone: ReadString(body, "maskedPhone", "masked_phone", "phoneMasked"),
                ExpiresAtUtc: null,
                ExpiresInSeconds: ReadInt(body, "expiresInSeconds", "expires_in_seconds"),
                ResendCooldownRemainingSeconds: ReadInt(body, "retryAfterSeconds", "resendCooldownRemainingSeconds", "resendCooldownSeconds", "cooldownSeconds"),
                GeneratedOtp: ReadString(body, "testOtpCode", "test_otp_code"),
                RejectionReason: rejectionReason);
        }

        var acceptedFlag = ReadBool(body, "accepted", "success", "isSuccess", "isValid", "sent", "isSent");
        var accepted = acceptedFlag ?? string.IsNullOrWhiteSpace(rejectionReason);
        return new ExternalPhoneOtpIssueResult(
            Succeeded: accepted,
            MaskedPhone: ReadString(body, "maskedPhone", "masked_phone", "phoneMasked"),
            ExpiresAtUtc: ReadDateTimeOffset(body, "expiresAtUtc", "expires_at_utc", "expiresAt"),
            ExpiresInSeconds: ReadInt(body, "expiresInSeconds", "expires_in_seconds"),
            ResendCooldownRemainingSeconds: ReadInt(body, "retryAfterSeconds", "resendCooldownRemainingSeconds", "resendCooldownSeconds", "cooldownSeconds"),
            GeneratedOtp: ReadString(body, "testOtpCode", "test_otp_code"),
            RejectionReason: accepted ? null : rejectionReason);
    }

    public async Task<ExternalPhoneOtpVerifyResult> VerifyAsync(
        ExternalPhoneOtpVerifyRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            _configuration.VerifyPath,
            new ExternalPhoneOtpVerifyPayload(
                request.TenantKey,
                request.PhoneNumber,
                request.Otp,
                request.ClientId),
            cancellationToken).ConfigureAwait(false);

        var responseBody = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        using var body = TryParseBody(responseBody);
        LogResponse(_configuration.VerifyPath, response, responseBody);
        var userNames = ReadStringArray(body, "userNames", "user_names");
        var rejectionReason = ReadString(body, "rejectionReason", "reason", "error_description", "errorDescription", "error", "message");

        if (response.IsSuccessStatusCode)
        {
            var success = ReadBool(body, "isValid", "accepted", "success", "isSuccess") ?? userNames.Count > 0;

            return new ExternalPhoneOtpVerifyResult(
                Succeeded: success,
                UserNames: userNames,
                RejectionReason: success ? null : rejectionReason);
        }

        return new ExternalPhoneOtpVerifyResult(
            Succeeded: false,
            UserNames: userNames,
            RejectionReason: rejectionReason);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string relativePath,
        object payload,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("ExternalPhoneOtpUserApi");
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(_configuration.ApiKeyHeaderName) &&
            !string.IsNullOrWhiteSpace(_configuration.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(_configuration.ApiKeyHeaderName.Trim(), _configuration.ApiKey.Trim());
        }

        _logger.LogInformation(
            "Calling external phone OTP API. Path={Path}, HasApiKey={HasApiKey}",
            relativePath,
            !string.IsNullOrWhiteSpace(_configuration.ApiKeyHeaderName) && !string.IsNullOrWhiteSpace(_configuration.ApiKey));

        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record ExternalPhoneOtpRequestPayload(
        [property: JsonPropertyName("tenant")] string Tenant,
        [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
        [property: JsonPropertyName("clientId")] string ClientId);

    private sealed record ExternalPhoneOtpVerifyPayload(
        [property: JsonPropertyName("tenant")] string Tenant,
        [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
        [property: JsonPropertyName("otpCode")] string OtpCode,
        [property: JsonPropertyName("clientId")] string ClientId);

    private void LogResponse(string path, HttpResponseMessage response, string? responseBody)
    {
        _logger.LogInformation(
            "External phone OTP API responded. Path={Path}, StatusCode={StatusCode}, BodyMasked={BodyMasked}",
            path,
            (int)response.StatusCode,
            MaskBody(responseBody));
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JsonDocument? TryParseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string MaskBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "<empty>";
        }

        return LongDigitRunRegex.Replace(body, static match =>
        {
            var value = match.Value;
            if (value.Length <= 4)
            {
                return value;
            }

            return new string('*', value.Length - 4) + value[^4..];
        });
    }

    private static string? ReadString(JsonDocument? body, params string[] names)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(body.RootElement, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String)
                {
                    return property.GetString();
                }

                if (property.ValueKind != JsonValueKind.Null && property.ValueKind != JsonValueKind.Undefined)
                {
                    return property.ToString();
                }
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonDocument? body, params string[] names)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(body.RootElement, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (property.ValueKind == JsonValueKind.False)
                {
                    return false;
                }

                if (property.ValueKind == JsonValueKind.String &&
                    bool.TryParse(property.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static int? ReadInt(JsonDocument? body, params string[] names)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(body.RootElement, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
                {
                    return parsed;
                }

                if (property.ValueKind == JsonValueKind.String &&
                    int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonDocument? body, params string[] names)
    {
        if (body is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetProperty(body.RootElement, name, out var property))
            {
                if (property.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonDocument? body, params string[] names)
    {
        if (body is null)
        {
            return Array.Empty<string>();
        }

        foreach (var name in names)
        {
            if (TryGetProperty(body.RootElement, name, out var property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                var values = new List<string>();
                foreach (var item in property.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values.Add(value);
                        }
                    }
                }

                return values;
            }
        }

        return Array.Empty<string>();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        if (element.TryGetProperty(name, out property))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }
}
