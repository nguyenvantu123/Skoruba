---
name: nswag-client-sync
description: Keep ASP.NET API contracts, DTOs, OpenAPI/NSwag generation, and client usage in sync.
triggers:
  - nswag
  - swagger client
  - dto sync
  - api client regeneration
inputs:
  - requirement
outputs:
  - synced DTOs/contracts/clients
  - validation notes
---

# Skill: nswag-client-sync

## Instructions
1. Inspect current API controller, request/response DTOs, validators, and mappings.
2. Inspect `nswag.json` or equivalent NSwag configuration before changing generation behavior.
3. Determine whether the contract change is backward-compatible.
4. Update DTOs and mappings first, then regenerate or adjust generated clients if required.
5. Check frontend or consuming projects for compile-time or runtime contract mismatches.
6. Report exactly which contracts changed.

## Watch for
- Generated client drift
- Nullable mismatch between server and client
- Enum serialization mismatches
- Accidental breaking changes in generated clients