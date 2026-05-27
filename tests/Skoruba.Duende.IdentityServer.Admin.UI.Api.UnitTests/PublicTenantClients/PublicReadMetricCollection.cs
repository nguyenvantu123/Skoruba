// Feature: tenant-client-cache-public-read, Task 5
//
// Defines an xUnit collection used by every test class that listens to
// the process-global "TenantClientCache" Meter via
// <see cref="TenantClientCache.Helpers.RecordingMeterListener"/>. xUnit
// runs distinct collections in parallel by default which causes counter
// increments emitted by one test to leak into another listener and break
// `ContainSingle()` style assertions. Sharing a single collection forces
// these classes to run sequentially.

#nullable enable

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

/// <summary>
/// Marker collection — opts every annotated test class out of inter-class
/// parallel execution so they never observe each other's
/// <c>tenant_client_cache.public_read.*</c> meter increments.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PublicReadMetricCollection
{
    public const string Name = "PublicTenantClients.PublicReadMetric";
}
