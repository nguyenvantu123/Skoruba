---
name: api-contract-change
description: Safely modify API contracts, DTOs, mappings, and callers.
triggers:
  - add API field
  - change request DTO
  - change response DTO
  - add endpoint
inputs:
  - requirement
outputs:
  - updated controller/service/DTO/mapping
  - compatibility notes
---

# Skill: api-contract-change

## Instructions
1. Identify the current endpoint, DTOs, validators, and mappings.
2. Check all known callers, especially UI client code.
3. Prefer backward-compatible contract changes unless explicitly asked to break compatibility.
4. Update API documentation/comments if present.
5. Verify serialization/deserialization implications.
6. Build both API project and affected callers if possible.

## Watch for
- Mapping breakage
- Nullability mismatches
- Silent frontend failures
- Versioning issues