---
name: migration-review
description: Review EF Core model and migration changes for correctness, provider safety, and production risk.
triggers:
  - review migration
  - ef migration review
  - schema review
inputs:
  - requirement
outputs:
  - migration assessment
  - risks
  - corrected migration/model changes if needed
---

# Skill: migration-review

## Instructions
1. Inspect entity/model changes, fluent configuration, DbContext, and existing migrations.
2. Determine whether the migration matches the actual model change.
3. Check provider-specific implications for SQL Server, PostgreSQL, or MySql projects.
4. Flag destructive or risky operations clearly.
5. If the migration is wrong, correct the model/config first rather than patching blindly.
6. Summarize deployment risk and rollback concerns.

## Watch for
- Wrong FK/index names
- Rename mistaken as drop/create
- Nullability/data loss risks
- Provider-specific type or default value drift