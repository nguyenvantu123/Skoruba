---
name: generate-technical-design
description: Produce a technical design before implementation for medium/large changes.
triggers:
  - design first
  - plan feature
  - architecture proposal
inputs:
  - requirement
outputs:
  - design doc
  - scope
  - sequence
  - affected components
---

# Skill: generate-technical-design

## Instructions
1. Produce a concise but implementation-ready design.
2. Include current state, proposed change, impacted layers, API/data changes, risks, and rollout notes.
3. Keep it specific to this repository.
4. Prefer a design that can be executed incrementally.