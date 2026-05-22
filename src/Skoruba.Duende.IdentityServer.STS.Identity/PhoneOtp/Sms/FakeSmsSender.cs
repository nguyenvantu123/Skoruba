using System.Collections.Concurrent;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Sms;

public sealed record FakeSentSms(string E164PhoneNumber, string Body, DateTimeOffset SentAtUtc);

public sealed class FakeSmsSender : ISmsSender
{
    private readonly ConcurrentBag<FakeSentSms> _sent = new();

    public IReadOnlyCollection<FakeSentSms> Sent => _sent.ToArray();

    public Task<SmsSendResult> SendAsync(string e164PhoneNumber, string body, CancellationToken cancellationToken)
    {
        _sent.Add(new FakeSentSms(e164PhoneNumber, body, DateTimeOffset.UtcNow));
        return Task.FromResult(SmsSendResult.Ok($"fake-{Guid.NewGuid():N}"));
    }
}
