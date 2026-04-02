---
name: skoruba-duende-admin-task
description: Implement or modify features in the Skoruba Duende IdentityServer Admin solution while preserving Duende/Identity/Admin architecture boundaries.
triggers:
  - skoruba task
  - duende admin task
  - client config change
  - identity admin change
  - configuration issues task
inputs:
  - requirement
outputs:
  - implementation
  - validation
  - summary
---

# Skill: skoruba-duende-admin-task

## Repository context
This repository contains:
- STS host: `src/Skoruba.Duende.IdentityServer.STS.Identity`
- Admin API: `src/Skoruba.Duende.IdentityServer.Admin.Api`
- Admin host/UI: `src/Skoruba.Duende.IdentityServer.Admin`
- React SPA client: `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
- Business logic: `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic*`
- EF stores: `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework*`
- Shared contracts/config: `src/Skoruba.Duende.IdentityServer.Shared*`
- Tenant helpers: `src/Skoruba.Duende.IdentityServer.TenantInfrastructure`

## Instructions
1. Inspect the existing Skoruba pattern before adding code.
2. Keep UI concerns in UI projects, HTTP concerns in API, business rules in BusinessLogic, and persistence in EntityFramework.
3. For IdentityServer client/resource/scope changes, inspect DTOs, services, mappings, validators, and UI callers.
4. For STS/login/session changes, explicitly review security implications.
5. For tenant-aware tasks, inspect `TenantInfrastructure` and ensure tenant isolation is preserved.
6. When changing admin screens, coordinate API contract changes and frontend DTO/client usage.
7. Run the narrowest useful validation first.

## Watch for
- Breaking OIDC/OAuth behavior silently
- Inconsistent DTO mapping between API and UI
- Cross-tenant leakage
- Direct EF usage from the wrong layer
- Incomplete updates across provider-specific EF projects