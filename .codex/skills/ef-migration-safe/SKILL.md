---
name: ef-migration-safe
description: Make EF Core model changes carefully and validate whether a migration is required and safe.
triggers:
  - entity change
  - db schema change
  - add field
  - add relation
inputs:
  - requirement
outputs:
  - entity/config changes
  - migration recommendation
  - migration or migration plan
---

# Skill: ef-migration-safe

## Instructions
1. Inspect current entity, configuration, DbContext, mapping, and repository usage.
2. Determine whether the requested change is schema-affecting.
3. If schema-affecting, explain the migration impact before generating it.
4. Check for provider-specific implications (SQL Server, PostgreSQL, MySql).
5. Avoid generating a migration unless the model change is real and consistent.
6. Verify compile/build for affected EF projects.

## Watch for
- Duplicate FK names
- Index conflicts
- Nullable vs required mismatches
- Provider-specific column type behavior
- Runtime breaking data changes