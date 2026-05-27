# Skoruba.Duende.IdentityServer.TenantClientCache.Client.UnitTests

Test project for the SDK NuGet package
`Skoruba.Duende.IdentityServer.TenantClientCache.Client` (feature
`tenant-client-cache-public-read`).

This project is bootstrapped at **Task 1** of the feature so package
versions and PBT conventions are pinned alongside the server-side foundation
work. The SDK csproj it tests against is created in **Task 7** of the same
feature; until then this project compiles as an empty test project (no
fixtures, no `ProjectReference`).

Test layout (created incrementally across Tasks 7–9):

- `Models/PublicClientSnapshotTests.cs` — Task 7
- `Models/PublicClientSnapshotProperties.cs` — Task 7 (P18)
- `Internal/TenantClientCacheClientRetryPolicyTests.cs` — Task 8
- `Internal/TenantClientCacheClientRetryPolicyProperties.cs` — Task 8 (P19)
- `Internal/TenantClientCacheClientMetricsTests.cs` — Task 8
- `TenantClientCacheClientTests.cs` — Task 9
- `TenantClientCacheClientCacheProperties.cs` — Task 9 (P20)
