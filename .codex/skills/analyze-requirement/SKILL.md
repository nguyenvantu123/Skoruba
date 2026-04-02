---
name: analyze-requirement
description: Analyze a user requirement, map it to solution layers, identify affected files, and produce an implementation plan before coding.
triggers:
  - new feature request
  - unclear requirement
  - cross-layer change
inputs:
  - requirement
outputs:
  - scope summary
  - impacted layers
  - impacted files/folders
  - implementation plan
  - risks
---

# Skill: analyze-requirement

## Purpose
Use this skill when a task starts from business language or natural language and must be translated into a code implementation plan.

## Instructions
1. Restate the requirement in technical terms.
2. Identify whether the request affects UI, API, BusinessLogic, EntityFramework, STS, or Shared layers.
3. List likely files and folders to inspect.
4. Determine whether the task is read-only analysis, code change, schema change, auth change, or UI change.
5. Produce a step-by-step plan before any broad implementation.
6. If architecture boundaries would be violated, propose a compliant path.

## Done when
- There is a concrete implementation plan.
- The likely impacted areas are identified.
- Risks and unknowns are noted.