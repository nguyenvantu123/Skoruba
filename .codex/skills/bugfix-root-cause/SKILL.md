---
name: bugfix-root-cause
description: Investigate a bug from symptoms/logs, identify root cause, implement a targeted fix, and validate it.
triggers:
  - bug
  - exception
  - failing build
  - wrong behavior
inputs:
  - bug report
  - logs
  - error message
outputs:
  - root cause summary
  - targeted code fix
  - validation result
---

# Skill: bugfix-root-cause

## Instructions
1. Reproduce conceptually from the provided symptoms.
2. Inspect the narrowest relevant code path first.
3. Identify the real root cause before changing code.
4. Avoid broad speculative edits.
5. Add or improve validation where practical.
6. Summarize symptom -> cause -> fix.

## Watch for
- Fixing the symptom instead of cause
- Breaking adjacent flows
- Missing config/environment assumptions