// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;

/// <summary>
/// Strongly typed options bound from the <c>TenantClientCache</c> configuration section.
/// Drives the tenant-scoped Duende Client snapshot cache (Admin_Api_Host) feature.
/// </summary>
/// <remarks>
/// Default values mirror the <c>Tenant_Client_Cache_Options</c> glossary entry (R1.2):
/// <list type="bullet">
///   <item><description><see cref="Enabled"/> = <c>true</c></description></item>
///   <item><description><see cref="AbsoluteTtl"/> = 01:00:00</description></item>
///   <item><description><see cref="SlidingTtl"/> = <c>null</c> (no sliding expiration)</description></item>
///   <item><description><see cref="RefreshInterval"/> = 01:00:00</description></item>
///   <item><description><see cref="WriteTimeoutMs"/> = 2000</description></item>
///   <item><description><see cref="MaxClientsPerTenant"/> = 5000</description></item>
/// </list>
/// </remarks>
public sealed class TenantClientCacheOptions
{
    /// <summary>
    /// Configuration section name (root key in <c>appsettings.json</c>).
    /// </summary>
    public const string SectionName = "TenantClientCache";

    /// <summary>
    /// Master toggle. When <c>false</c>, the tenant client cache service is a no-op
    /// (read/write/invalidate) and the background refresh hosted service is not registered.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hard cap on per-snapshot lifetime in Redis. Applied as
    /// <see cref="Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions.AbsoluteExpirationRelativeToNow"/>
    /// on every write. Valid range: <c>[00:05:00, 24:00:00]</c>.
    /// </summary>
    public TimeSpan AbsoluteTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional sliding lifetime applied as
    /// <see cref="Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions.SlidingExpiration"/>.
    /// When <c>null</c>, sliding expiration is disabled. When non-null, valid range is
    /// <c>[00:01:00, AbsoluteTtl]</c>.
    /// </summary>
    public TimeSpan? SlidingTtl { get; set; } = null;

    /// <summary>
    /// Period between background sweeps that rebuild the cache from the database.
    /// Valid range: <c>[00:05:00, 24:00:00]</c>.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum wall-clock time (in milliseconds) for a single
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/> operation.
    /// Bound enforced via a linked <see cref="System.Threading.CancellationTokenSource"/>.
    /// Valid range: <c>[100, 10000]</c>.
    /// </summary>
    public int WriteTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// Safety cap on how many clients per tenant the background sweep will materialize per cycle.
    /// Valid range: <c>[1, 50000]</c>.
    /// </summary>
    public int MaxClientsPerTenant { get; set; } = 5000;
}
