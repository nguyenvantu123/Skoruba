---
name: multi-tenant-safety-check
description: Review and implement multi-tenant changes safely, preserving tenant isolation, tenant-aware filtering, and configuration boundaries.
triggers:
  - multi tenant
  - tenant aware
  - tenant isolation
  - tenant setup
inputs:
  - requirement
outputs:
  - safe implementation plan
  - risks
  - code changes if requested
---

# Skill: multi-tenant-safety-check

## Instructions
1. Inspect `src/Skoruba.Duende.IdentityServer.TenantInfrastructure` first.
2. Identify how tenant context is resolved and propagated.
3. Check API endpoints, services, repositories, and queries for tenant scoping.
4. Ensure no read or write can cross tenant boundaries.
5. Verify whether DTOs, caches, or UI state need tenant-awareness.
6. If a requirement is ambiguous, choose the least-privilege tenant-safe implementation path.
7. Explicitly call out isolation risks before coding.

## Watch for
- Missing tenant filter in queries
- Shared cache keys without tenant dimension
- Admin actions affecting the wrong tenant
- Global config accidentally applied per tenant or vice versa