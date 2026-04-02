---
name: fullstack-enterprise-implementer
description: End-to-end implementation skill for enterprise .NET repos. It analyzes requirements, maps layers, implements safely, validates changes, and summarizes output.
triggers:
  - implement this
  - build this feature
  - code this requirement
inputs:
  - requirement
outputs:
  - implementation
  - validation
  - summary
---

# Skill: fullstack-enterprise-implementer

## Workflow
1. Run requirement analysis.
2. Inspect relevant files.
3. If auth-related, run security review.
4. If API-related, update DTOs/mappings/callers consistently.
5. If EF-related, assess migration safety.
6. Implement the smallest coherent change set.
7. Run focused validation.
8. Summarize changes, files, validation, and risks.

## Constraints
- Respect AGENTS.md.
- Keep controllers thin.
- Keep domain rules in BusinessLogic.
- Keep persistence in EntityFramework.
- Keep UI consistent with existing patterns.