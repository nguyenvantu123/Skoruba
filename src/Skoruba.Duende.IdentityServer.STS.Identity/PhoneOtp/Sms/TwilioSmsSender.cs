using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;

public sealed class TwilioSmsSender : ISmsSender
{
    private readonly SmsTwilioConfiguration _config;
    private readonly ILogger<TwilioSmsSender> _logger;

    public TwilioSmsSender(IOptions<SmsTwilioConfiguration> options, ILogger<TwilioSmsSender> logger)
    {
        _config = options.Value;
        _logger = logger;
    }

    public async Task<SmsSendResult> SendAsync(string e164PhoneNumber, string body, CancellationToken cancellationToken)
    {
        TwilioClient.Init(_config.AccountSid, _config.AuthToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(_config.TimeoutMilliseconds));

        var toMasked = MaskPhoneNumber(e164PhoneNumber);
        var fromMasked = MaskPhoneNumber(_config.FromNumber);

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                var msg = await MessageResource.CreateAsync(
                    to: new PhoneNumber(e164PhoneNumber),
                    from: new PhoneNumber(_config.FromNumber),
                    body: body,
                    client: TwilioClient.GetRestClient());

                _logger.LogInformation(
                    "Twilio SMS sent successfully. ToMasked={ToMasked}, FromMasked={FromMasked}, MessageSid={MessageSid}",
                    toMasked,
                    fromMasked,
                    msg.Sid);

                return SmsSendResult.Ok(msg.Sid);
            }
            catch (ApiException ex) when (IsRetryable(ex) && attempt < _config.MaxRetries)
            {
                _logger.LogWarning(
                    "Twilio transient error, retrying. ErrorCode={ErrorCode}, Attempt={Attempt}, ToMasked={ToMasked}, FromMasked={FromMasked}",
                    ex.Code,
                    attempt,
                    toMasked,
                    fromMasked);
            }
            catch (ApiException ex)
            {
                _logger.LogError(
                    "Twilio send failed. ErrorCode={ErrorCode}, ToMasked={ToMasked}, FromMasked={FromMasked}",
                    ex.Code,
                    toMasked,
                    fromMasked);

                return SmsSendResult.Failed(ex.Code.ToString(), ex.Message);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(
                    "Twilio send timed out. ToMasked={ToMasked}, FromMasked={FromMasked}",
                    toMasked,
                    fromMasked);

                return SmsSendResult.Failed("timeout", "SMS send timed out.");
            }
            catch (Exception ex) when (attempt < _config.MaxRetries)
            {
                _logger.LogWarning(
                    ex,
                    "Twilio network/IO error, retrying. Attempt={Attempt}, ToMasked={ToMasked}, FromMasked={FromMasked}",
                    attempt,
                    toMasked,
                    fromMasked);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Twilio send failed after retries. ToMasked={ToMasked}, FromMasked={FromMasked}",
                    toMasked,
                    fromMasked);

                return SmsSendResult.Failed("network", ex.Message);
            }
        }

        // Should not reach here, but safety net
        return SmsSendResult.Failed("max-retries", "Twilio send failed after retry.");
    }

    private static bool IsRetryable(ApiException ex)
    {
        // HTTP 5xx from Twilio = transient server error
        if (ex.Status >= 500)
            return true;

        // Twilio code 20429 = Too Many Requests (transient, retryable)
        if (ex.Code == 20429)
            return true;

        // All other codes (e.g. 20003 = auth failure) are permanent
        return false;
    }

    private static string MaskPhoneNumber(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return "<empty>";
        }

        var trimmed = phoneNumber.Trim();
        if (trimmed.Length <= 4)
        {
            return new string('*', trimmed.Length);
        }

        return $"{trimmed[..Math.Min(3, trimmed.Length)]}***{trimmed[^4..]}";
    }
}
