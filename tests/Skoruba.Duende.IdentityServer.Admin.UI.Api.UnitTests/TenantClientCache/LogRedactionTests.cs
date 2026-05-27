// Feature: tenant-client-cache-expansion, Task 5
//
// Pin the SanitizeExceptionMessage redaction + truncation contract used by
// TenantClientCacheService.EmitAudit. Every assertion mirrors a Task 5
// bullet: 256-char truncation + case-insensitive redaction of `password=`
// and `auth=` patterns.
//
// Validates: Requirements 13.4

#nullable enable

using System;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.TenantClientCache;

public sealed class LogRedactionTests
{
    [Fact]
    public void Null_Exception_Returns_Empty_String()
    {
        LogRedaction.SanitizeExceptionMessage(null).Should().BeEmpty();
    }

    [Fact]
    public void Plain_Message_Roundtrips_Verbatim()
    {
        var ex = new InvalidOperationException("connection refused");

        LogRedaction.SanitizeExceptionMessage(ex).Should().Be("connection refused");
    }

    [Fact]
    public void Password_Token_Is_Redacted_Case_Insensitively()
    {
        var ex = new InvalidOperationException(
            "server=localhost,Password=topsecret,db=app");

        var redacted = LogRedaction.SanitizeExceptionMessage(ex);

        redacted.Should().NotContain("topsecret");
        redacted.Should().Contain("***");
    }

    [Fact]
    public void Auth_Token_Is_Redacted_Case_Insensitively()
    {
        var ex = new InvalidOperationException("auth=BEARER-XYZ failed");

        var redacted = LogRedaction.SanitizeExceptionMessage(ex);

        redacted.Should().NotContain("BEARER-XYZ");
        redacted.Should().Contain("***");
    }

    [Fact]
    public void Multiple_Credential_Patterns_Are_All_Redacted()
    {
        var ex = new InvalidOperationException(
            "connstr=server,password=topsecret,auth=AAA;db=app");

        var redacted = LogRedaction.SanitizeExceptionMessage(ex);

        redacted.Should().NotContain("topsecret");
        redacted.Should().NotContain("AAA");
    }

    [Fact]
    public void Long_Message_Is_Truncated_To_256_Characters()
    {
        // 1024 chars; the redactor must hard-truncate to its 256 ceiling.
        var longMessage = new string('a', 1024);
        var ex = new InvalidOperationException(longMessage);

        var redacted = LogRedaction.SanitizeExceptionMessage(ex);

        redacted.Length.Should().Be(LogRedaction.MaxMessageLength);
        redacted.Length.Should().Be(256);
    }

    [Fact]
    public void Truncation_Limit_Matches_Documented_Constant()
    {
        // R13.4: hard ceiling of 256 chars. Pin the constant so a future
        // refactor cannot quietly raise it without updating the spec.
        LogRedaction.MaxMessageLength.Should().Be(256);
    }
}
