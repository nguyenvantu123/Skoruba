---
name: dotnet-feature-implementation
description: Implement a .NET feature end-to-end while preserving architecture boundaries and validating the result.
triggers:
  - implement feature
  - add endpoint
  - add service
  - extend business logic
inputs:
  - requirement
outputs:
  - code changes
  - tests or validation
  - summary of files changed
---

# Skill: dotnet-feature-implementation

## Instructions
1. Use `analyze-requirement` first for non-trivial tasks.
2. Inspect existing patterns in the relevant project before adding new code.
3. Prefer extending existing services/interfaces over introducing parallel abstractions.
4. Keep controllers thin.
5. Put business rules into BusinessLogic.
6. Put persistence changes into EntityFramework layer.
7. Update DTO mappings if API contracts change.
8. Run focused build/test commands after changes.
9. Report exact files changed and validation steps.

## Constraints
- Avoid architectural shortcuts.
- Avoid mixing UI concerns into API or BusinessLogic.
- Avoid hidden breaking changes.