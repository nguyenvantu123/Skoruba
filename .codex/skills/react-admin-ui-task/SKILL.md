---
name: react-admin-ui-task
description: Implement or refine UI behavior in the React admin SPA with consistency to existing patterns.
triggers:
  - ui change
  - form change
  - page change
  - client-side validation
inputs:
  - requirement
outputs:
  - UI code changes
  - integration notes with API
---

# Skill: react-admin-ui-task

## Instructions
1. Inspect routing, page structure, existing components, and API client patterns.
2. Reuse shared UI components and style conventions where possible.
3. Preserve accessibility and loading/error states.
4. If API data shape changes, coordinate with `api-contract-change`.
5. Build frontend if possible.

## Watch for
- Unhandled loading/error states
- Diverging UI patterns
- Mismatch with backend DTOs