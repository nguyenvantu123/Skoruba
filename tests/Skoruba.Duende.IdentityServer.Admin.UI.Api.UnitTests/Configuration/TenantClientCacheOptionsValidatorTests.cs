// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Globalization;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.Configuration;

/// <summary>
/// Unit tests for <see cref="TenantClientCacheOptionsValidator"/>.
/// Covers Requirements R1.2 – R1.7. Each failure case asserts the message names the
/// configuration key path AND contains the observed value (R1.3 – R1.6).
/// </summary>
public class TenantClientCacheOptionsValidatorTests
{
    private const string Section = TenantClientCacheOptions.SectionName;

    private static TenantClientCacheOptions Defaults() => new();

    private static ValidateOptionsResult Validate(TenantClientCacheOptions options)
    {
        var validator = new TenantClientCacheOptionsValidator();
        return validator.Validate(name: null, options);
    }

    [Fact]
    public void Defaults_AreValid()
    {
        // R1.2: defaults documented in Glossary Tenant_Client_Cache_Options must validate.
        var options = Defaults();

        var result = Validate(options);

        result.Succeeded.Should().BeTrue(
            "default configuration must satisfy all range guards (R1.2)");
        result.Failures.Should().BeNull();
    }

    [Fact]
    public void AbsoluteTtl_Below_5min_Fails_NamesKeyAndValue()
    {
        // R1.3: AbsoluteTtl ∈ [00:05:00, 24:00:00]
        var options = Defaults();
        options.AbsoluteTtl = TimeSpan.FromMinutes(1);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.AbsoluteTtl)}");
        failure.Should().Contain(options.AbsoluteTtl.ToString("c", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void AbsoluteTtl_Above_24h_Fails_NamesKeyAndValue()
    {
        // R1.3: upper bound check.
        var options = Defaults();
        options.AbsoluteTtl = TimeSpan.FromHours(25);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.AbsoluteTtl)}");
        failure.Should().Contain(options.AbsoluteTtl.ToString("c", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SlidingTtl_Null_Allowed()
    {
        // R1.4: SlidingTtl null means sliding expiration disabled — must validate.
        var options = Defaults();
        options.SlidingTtl = null;

        var result = Validate(options);

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void SlidingTtl_Below_1min_Fails()
    {
        // R1.4: SlidingTtl ∈ [00:01:00, AbsoluteTtl] when non-null.
        var options = Defaults();
        options.SlidingTtl = TimeSpan.FromSeconds(30);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.SlidingTtl)}");
        failure.Should().Contain(options.SlidingTtl!.Value.ToString("c", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SlidingTtl_Greater_Than_AbsoluteTtl_Fails()
    {
        // R1.4: cross-field guard — SlidingTtl must not exceed AbsoluteTtl.
        var options = Defaults();
        options.AbsoluteTtl = TimeSpan.FromMinutes(10);
        options.SlidingTtl = TimeSpan.FromMinutes(30);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.SlidingTtl)}");
        failure.Should().Contain(options.SlidingTtl!.Value.ToString("c", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("00:04:00")]      // below min (5 min)
    [InlineData("25:00:00")]      // above max (24 h)
    public void RefreshInterval_OutOfRange_Fails(string refreshInterval)
    {
        // R1.5: RefreshInterval ∈ [00:05:00, 24:00:00]
        var options = Defaults();
        options.RefreshInterval = TimeSpan.Parse(refreshInterval, CultureInfo.InvariantCulture);

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.RefreshInterval)}");
        failure.Should().Contain(options.RefreshInterval.ToString("c", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(99)]      // below min (100)
    [InlineData(10_001)]  // above max (10_000)
    public void WriteTimeoutMs_OutOfRange_Fails_LowAndHigh(int writeTimeoutMs)
    {
        // R1.6: WriteTimeoutMs ∈ [100, 10000]
        var options = Defaults();
        options.WriteTimeoutMs = writeTimeoutMs;

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.WriteTimeoutMs)}");
        failure.Should().Contain(writeTimeoutMs.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0)]        // below min (1)
    [InlineData(50_001)]   // above max (50_000)
    public void MaxClientsPerTenant_OutOfRange_Fails_LowAndHigh(int maxClientsPerTenant)
    {
        // R1.6: MaxClientsPerTenant ∈ [1, 50000]
        var options = Defaults();
        options.MaxClientsPerTenant = maxClientsPerTenant;

        var result = Validate(options);

        result.Failed.Should().BeTrue();
        var failure = result.Failures!.Single();
        failure.Should().Contain($"{Section}:{nameof(TenantClientCacheOptions.MaxClientsPerTenant)}");
        failure.Should().Contain(maxClientsPerTenant.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Disabled_SkipsRangeChecks_Succeeds()
    {
        // R1.7 / R1.8: when Enabled=false the cache is a no-op and range checks are skipped.
        var options = new TenantClientCacheOptions
        {
            Enabled = false,
            // Deliberately invalid values — must still validate because Enabled=false.
            AbsoluteTtl = TimeSpan.FromSeconds(1),
            SlidingTtl = TimeSpan.FromHours(48),
            RefreshInterval = TimeSpan.FromDays(2),
            WriteTimeoutMs = 0,
            MaxClientsPerTenant = -1,
        };

        var result = Validate(options);

        result.Succeeded.Should().BeTrue(
            "when Enabled=false the validator must short-circuit (R1.7/R1.8)");
    }
}
