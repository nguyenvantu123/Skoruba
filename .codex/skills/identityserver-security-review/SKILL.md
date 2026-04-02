---
name: identityserver-security-review
description: Review IdentityServer or auth-related changes for security and configuration risks before implementation.
triggers:
  - auth change
  - token change
  - login flow change
  - client security review
inputs:
  - requirement
outputs:
  - security review
  - recommended changes
  - risky areas highlighted
---

# Skill: identityserver-security-review

## Instructions
1. Inspect STS, relevant client config, token settings, login/logout flow, and cookie/OIDC behavior.
2. Explicitly identify security-sensitive settings.
3. Distinguish convenience changes from security changes.
4. If the requirement weakens security, flag it clearly before coding.
5. Prefer least-privilege and minimal-scope changes.

## Watch for
- Silent token lifetime changes
- Insecure redirect URIs
- Over-broad scopes
- Session/cookie behavior side effects
- Multi-tenant isolation issues