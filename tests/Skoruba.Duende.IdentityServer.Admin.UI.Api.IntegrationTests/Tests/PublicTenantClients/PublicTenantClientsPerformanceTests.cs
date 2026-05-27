// Feature: tenant-client-cache-public-read, Task 10
//
// Performance smoke test for the public-read endpoint. Drives 1000
// authenticated GET requests against the in-process WebApplicationFactory
// with the FakeTenantClientCacheService returning a canned envelope and
// asserts the wall-clock p99 stays under the design budget (≤ 25 ms).
//
// The test is tagged Performance so a future CI environment can skip it
// with `--filter Category!=Performance`. By default it MUST PASS — the
// in-memory pipeline (no socket, no Redis) easily clears 25 ms / request
// on developer hardware.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using FluentAssertions;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients.Helpers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.TenantClientCache;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.PublicTenantClients;

[Trait("Category", "Performance")]
public sealed class PublicTenantClientsPerformanceTests
{
    private const string Tenant = "acme";
    private const string Client = "web";

    private static double Percentile(IList<double> samples, double p)
    {
        if (samples.Count == 0) return 0;
        var sorted = samples.OrderBy(x => x).ToArray();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        rank = Math.Clamp(rank, 0, sorted.Length - 1);
        return sorted[rank];
    }

    [Fact]
    public async Task Performance_PublicRead_P99_Under_25ms_With_MemoryDistributedCache()
    {
        using var host = new PublicTenantClientsTestHost.Builder()
            .WithApiKey(Tenant, TestApiKeys.ValidHashAcme)
            // Loosen the rate limit so the 1000-iteration drive does
            // not trip the partition.
            .WithRateLimit(tokenLimit: 10000, tokensPerPeriod: 10000, replenishmentPeriod: TimeSpan.FromSeconds(1))
            .Build();
        host.FakeCache.WhenAnyKey_Returns(new ClientCacheSnapshotEnvelope
        {
            Version = 1,
            TenantKey = Tenant,
            ClientId = Client,
            LastWriteUtc = new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc),
            Data = new ClientCacheSnapshotDto
            {
                ClientId = Client,
                ClientName = "Sample",
                ProtocolType = "oidc",
                Enabled = true,
                AccessTokenLifetime = 3600,
                RedirectUris = new[] { "https://app/callback" },
                AllowedScopes = new[] { "openid" },
                LastWriteUtc = new DateTime(2024, 5, 1, 12, 30, 45, DateTimeKind.Utc),
            },
        });

        // Warm-up to amortise JIT.
        for (var i = 0; i < 50; i++)
        {
            using var warm = await SendAsync(host);
            warm.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        const int Iterations = 1000;
        var samples = new List<double>(Iterations);
        for (var i = 0; i < Iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            using var resp = await SendAsync(host);
            sw.Stop();
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        var p99 = Percentile(samples, 99);
        p99.Should().BeLessThan(25.0,
            $"public-read p99 must stay under the design budget of 25 ms (observed {p99:F3} ms)");
    }

    private static Task<HttpResponseMessage> SendAsync(PublicTenantClientsTestHost host)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/public/tenants/{Tenant}/clients/{Client}");
        req.Headers.Add("X-Tenant-Api-Key", TestApiKeys.ValidPlaintext);
        return host.Client.SendAsync(req);
    }
}
