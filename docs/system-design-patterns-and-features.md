# System Design Patterns And Features

This document gives a practical overview of the solution architecture, the main design patterns used in the repository, and the core functional areas currently implemented.

It is intended for:

- developers joining the project
- engineers extending `Duende IdentityServer` and admin features
- reviewers who need to understand where logic belongs
- operators who need a system-level map of runtime responsibilities

Related documents:

- [architecture.md](E:\Skoruba\docs\architecture.md)
- [phone-otp-multi-account.md](E:\Skoruba\docs\phone-otp-multi-account.md)
- [public-tenant-api.md](E:\Skoruba\docs\public-tenant-api.md)
- [tenant-client-cache.md](E:\Skoruba\docs\tenant-client-cache.md)

## 1. System Purpose

The solution is a multi-tenant identity and administration platform built around:

- `Duende IdentityServer` for authentication and authorization
- `ASP.NET Core Identity` for user management
- a React-based Admin UI for operational management
- REST APIs for configuration, identity administration, and public tenant/client discovery
- tenant-aware infrastructure so multiple tenants can share platform capabilities while keeping identity and configuration boundaries isolated

At a high level, the platform supports:

- identity management
- IdentityServer client/resource administration
- tenant-aware login and client resolution
- password and passwordless authentication flows
- external integration through public-safe APIs
- operational diagnostics and configuration governance

## 2. Runtime Services

The primary runnable services are:

- `Skoruba.Duende.IdentityServer.STS.Identity`
  - the Security Token Service
  - hosts `Duende IdentityServer`
  - performs login, logout, cookie/session handling, and token issuance

- `Skoruba.Duende.IdentityServer.Admin.Api`
  - main admin REST API
  - used by the UI and administration flows to manage clients, resources, users, and operational data

- `Skoruba.Duende.IdentityServer.Admin`
  - ASP.NET Core host for the Admin SPA
  - handles OIDC login to the STS and serves the React application

Additional solution components provide:

- shared DTOs and configuration helpers
- tenant infrastructure
- UI-facing helper APIs
- optional public client/tenant discovery surfaces
- mobile/BFF and bootstrap scenarios

## 3. Layered Architecture

The repository follows a layered architecture with clear separation between presentation, API, business rules, persistence, and authentication.

### 3.1 Presentation Layer

Primary projects:

- `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
- `src/Skoruba.Duende.IdentityServer.Admin`
- `src/Skoruba.Duende.IdentityServer.Admin.UI`
- `src/Skoruba.Duende.IdentityServer.Admin.UI.Spa`
- `src/Skoruba.Duende.IdentityServer.STS.Identity` views and static assets

Responsibilities:

- user experience and page composition
- login and verification screens
- SPA routing and form handling
- calling API endpoints
- surfacing validation and operational messages

Presentation code should not own business rules or persistence logic.

### 3.2 API Layer

Primary projects:

- `src/Skoruba.Duende.IdentityServer.Admin.Api`
- `src/Skoruba.Duende.IdentityServer.Admin.UI.Api`

Responsibilities:

- external HTTP contracts
- request validation and mapping
- authentication and authorization checks
- orchestration of business logic services
- public-safe API surfaces for tenant/client lookup

Controllers should stay thin and defer rules to services where possible.

### 3.3 Business Logic Layer

Primary projects:

- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic`
- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity`
- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared`

Responsibilities:

- domain rules
- validation
- orchestration between API and persistence
- mapping between entities and DTOs
- administrative workflows
- cross-cutting policies and monitoring rules

### 3.4 Data Access Layer

Primary projects:

- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.*`

Responsibilities:

- EF Core contexts
- entity mapping
- migrations
- provider-specific storage wiring
- persistence helpers and repository-style abstractions

The solution supports multiple providers across the EF packages, including MySQL and other relational backends.

### 3.5 STS Layer

Primary project:

- `src/Skoruba.Duende.IdentityServer.STS.Identity`

Responsibilities:

- host `Duende IdentityServer`
- perform sign-in and sign-out
- manage cookie authentication
- continue `authorize` and `signin-oidc` flows
- run custom login extensions such as phone OTP
- integrate tenant-aware behavior into identity flows

### 3.6 Shared And Tenant Infrastructure

Primary projects:

- `src/Skoruba.Duende.IdentityServer.Shared`
- `src/Skoruba.Duende.IdentityServer.Shared.Configuration`
- `src/Skoruba.Duende.IdentityServer.TenantInfrastructure`

Responsibilities:

- shared contracts
- options/configuration models
- tenant context access
- tenant-aware helpers and bootstrap services
- cross-service utilities

## 4. Design Patterns Used In The Solution

This solution is not built around a single formal pattern. It uses a practical combination of common enterprise patterns.

### 4.1 Layered Architecture

The dominant top-level pattern is classic layered architecture:

- presentation
- API
- business logic
- persistence
- shared infrastructure

This keeps UI concerns, transport concerns, and persistence concerns separated.

### 4.2 Composition Root / Dependency Injection

Each application host uses ASP.NET Core dependency injection as the composition root.

Typical uses:

- service registration in `Startup` or service collection extensions
- choosing providers based on configuration
- wiring STS-specific auth components
- swapping implementations for dev, test, and production

This pattern is heavily used in:

- OTP service wiring
- tenant snapshot providers
- admin services
- SMS providers
- public client cache consumers

### 4.3 Options Pattern

Configuration is modeled using strongly typed options classes rather than hard-coded values.

Examples include:

- admin authentication configuration
- phone OTP settings
- Twilio settings
- tenant client cache settings
- user API settings
- public snapshot consumer settings

Benefits:

- centralizes configuration shape
- reduces magic strings in business code
- allows environment-specific overrides
- supports feature flags and fail-fast startup validation

### 4.4 Adapter Pattern

External integrations are wrapped behind local abstractions.

Examples:

- `IExternalPhoneOtpClient` wraps the `UserApi` contract
- SMS sending is abstracted behind sender interfaces
- public tenant/client snapshot providers wrap downstream APIs

Benefits:

- isolates external API contracts
- keeps calling code stable when integration changes
- improves testability

### 4.5 Strategy / Provider Pattern

Multiple implementations can fulfill the same capability based on configuration.

Examples:

- OTP persistence providers
  - distributed cache / Redis-backed
  - MongoDB-backed
- SMS delivery
  - Twilio sender
  - fake/dev sender
- public snapshot provider
  - enabled provider
  - disabled/no-op provider

This allows the system to swap infrastructure behavior without changing controller flow.

### 4.6 DTO And Mapping Pattern

The solution uses DTOs and mapping boundaries between layers.

Typical mapping boundaries:

- HTTP request/response DTOs
- business model DTOs
- EF entities
- external API payload DTOs

Benefits:

- protects domain/persistence internals from leaking outward
- makes contract changes explicit
- supports versioning and safer refactoring

### 4.7 Fail-Soft And Feature-Flag Pattern

Several optional subsystems are enabled by configuration and fail soft where possible.

Examples:

- public tenant client snapshot consumer
- phone OTP login
- multi-account selection
- test OTP exposure in dev flows

The system prefers:

- explicit enable/disable flags
- safe defaults
- no-op implementations when disabled
- strong logging when active integrations fail

### 4.8 Codec / Protected-Payload Pattern

Sensitive transient state is stored in protected cookies using codecs and `IDataProtection`.

Examples:

- phone OTP session cookie
- account select cookie

Benefits:

- reduces direct trust in browser-supplied raw state
- keeps temporary state portable across requests
- avoids exposing internal identifiers in plain text

### 4.9 PRG And Continuation Pattern

Authentication and verification flows follow continuation-based navigation patterns:

- `authorize` -> login -> continue
- verify -> sign-in -> continue
- multi-account selection -> continue

This keeps the STS aligned with `Duende IdentityServer` expectations and downstream OIDC client behavior.

## 5. Core Functional Areas

### 5.1 Identity And Authentication

Implemented in the STS host and Identity integration layers.

Core functions:

- username/password login
- sign-out
- cookie authentication
- OIDC/OAuth2 authorization continuation
- downstream client login compatibility
- tenant-aware authentication behavior

### 5.2 Duende IdentityServer Administration

Implemented primarily across Admin UI, Admin API, BusinessLogic, and EF layers.

Core functions:

- manage clients
- manage API resources
- manage identity resources
- manage user and admin data
- monitor configuration quality and policy issues
- support administrative workflows through REST + SPA

### 5.3 Tenant-Aware Infrastructure

The solution includes tenant-aware behavior in both runtime and integration layers.

Core functions:

- resolve current tenant context
- isolate tenant data and auth flow behavior
- expose public tenant discovery where needed
- resolve tenant-specific client metadata
- support tenant-aware login behavior and account resolution

### 5.4 Public Tenant And Client Discovery

The repository contains public-safe APIs and consumer components for:

- public tenant list discovery
- tenant client metadata lookup
- bootstrap scenarios for external systems
- mobile/BFF or external app pre-auth configuration

These flows avoid exposing sensitive admin internals while still allowing bootstrap of client-facing integrations.

### 5.5 Mobile / BFF Bootstrap

The repository includes a mobile/BFF-oriented flow where downstream clients can fetch client metadata before they have a user token.

This is separate from the main STS login flow and is intended for bootstrap/runtime configuration scenarios.

### 5.6 Phone OTP Passwordless Login

One of the main custom authentication extensions in this repository is phone-based passwordless login.

The current implementation supports:

- request OTP
- verify OTP
- optional dev/test OTP display
- resend OTP with cooldown
- multi-account selection when multiple users share a phone number inside the same tenant
- sign-in continuation through existing `Duende IdentityServer` flows

## 6. Current Phone OTP Architecture

This section reflects the current flow as implemented in `STS.Identity`.

### 6.1 Current Integration Model

The STS no longer needs to generate or validate the OTP itself in the active external-OTP flow.

Instead:

- Skoruba UI submits the phone login request to the STS
- the STS calls an external `UserApi`
- `UserApi` owns OTP send/request and OTP validation
- after successful verification, the STS resolves the local user by `userNames`
- the STS finishes login using the existing IdentityServer continuation

### 6.2 Request OTP Contract

Current external request contract:

`POST {UserApiBaseUrl}/connect/phone-otp/request`

Request JSON:

```json
{
  "tenant": "tenant1",
  "phoneNumber": "+84334336232",
  "clientId": "webapp"
}
```

Success response:

```json
{
  "sent": true,
  "expiresAtUtc": "2026-05-31T16:53:20+00:00",
  "expiresInSeconds": 300,
  "retryAfterSeconds": 60,
  "testOtpCode": "123456"
}
```

Notes:

- `testOtpCode` is only expected in development-like scenarios when SMS is intentionally disabled and the external system exposes the code for testing.
- the STS stores transient verification state in a protected session cookie, including:
  - tenant key
  - phone hash
  - phone number
  - client id
  - masked phone
  - expiry
  - resend availability time

### 6.3 Verify OTP Contract

Current external verify contract:

`POST {UserApiBaseUrl}/connect/phone-otp/verify`

Request JSON:

```json
{
  "tenant": "tenant1",
  "phoneNumber": "+84334336232",
  "otpCode": "123456",
  "clientId": "webapp"
}
```

Success response:

```json
{
  "isValid": true,
  "userNames": [
    "username"
  ]
}
```

Failure response:

```json
{
  "isValid": false,
  "userNames": [],
  "error": "The OTP code is invalid or expired."
}
```

### 6.4 Verify Continuation Rules

After `isValid = true`:

- if `userNames` contains exactly one username:
  - the STS resolves the local user in the current tenant
  - signs the user in through ASP.NET Core Identity
  - continues the original Duende login flow

- if `userNames` contains multiple usernames:
  - the STS shows the existing account selection screen
  - the user chooses the account to continue with

The STS does not call `/connect/token` for OTP login in this design.

### 6.5 Resend Behavior

Resend uses the active phone session state already held by the STS:

- the STS reuses the protected session cookie
- calls the same external request endpoint
- updates expiry and cooldown state
- shows test OTP again in development if present

### 6.6 UX Behavior

The current verification UX includes:

- redesigned verify screen
- masked phone display
- resend countdown
- client-side inline verify error behavior
- no full page reload when OTP is wrong
- redirect continuation when OTP is correct

Current intended behavior:

- wrong OTP
  - stay on the same verify page
  - keep the current input/page state
  - only show the error text

- correct OTP with one account
  - sign in immediately
  - continue the original login flow

- correct OTP with multiple accounts
  - redirect to account selection

## 7. Key Security And Operational Principles

### 7.1 Tenant Isolation

Authentication and account resolution must stay tenant-aware.

Important rules:

- a phone session cookie is bound to tenant context
- a verified username must still resolve to a user within the same tenant
- account selection must not leak cross-tenant candidates

### 7.2 Redirect Safety

The STS continues login only through allowed continuation logic:

- authorization context from Duende
- local return URLs
- safe fallback to root

Untrusted return URLs must not be blindly redirected.

### 7.3 Masked Diagnostics

Sensitive flows use masked logging where possible.

Never log:

- raw OTP codes in production diagnostics
- raw protected cookie contents
- unnecessary sensitive identifiers

Prefer:

- masked phone numbers
- hashed identifiers
- structured event names and outcomes

### 7.4 Feature Flags And Safe Defaults

Optional features should remain flag-driven:

- phone OTP login
- multi-account selection
- public snapshot consumers
- test OTP display

Safe defaults are preferred over implicit activation.

### 7.5 External Integration Boundaries

External services such as `UserApi` and Twilio should remain behind local abstractions.

This keeps:

- controller logic stable
- integration contracts isolated
- diagnostics centralized
- blast radius smaller when external APIs change

## 8. Main Integration Points

### 8.1 Duende IdentityServer

Used for:

- authorization flows
- return URL continuation
- cookie-based login session behavior
- client compatibility for downstream applications

### 8.2 ASP.NET Core Identity

Used for:

- local user storage
- sign-in operations
- lockout checks
- user lookup after OTP verification

### 8.3 External UserApi

Used for:

- OTP request
- OTP verification
- userNames resolution payload for continuation

### 8.4 Twilio

Twilio remains relevant for environments where SMS is actually delivered through the internal OTP path or external OTP provider chain, though the active STS flow can bypass direct local SMS sending when delegated to `UserApi`.

### 8.5 Redis / Distributed Cache

Used for:

- OTP-related transient state in some flows
- cooldown/rate limiting infrastructure
- provider-backed state in legacy or configurable paths

### 8.6 MongoDB

Used as an optional OTP record persistence backend through the OTP store abstraction.

## 9. Where To Change What

Use this map when implementing new requirements.

### 9.1 UI / UX Changes

Change:

- `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
- `src/Skoruba.Duende.IdentityServer.STS.Identity/Views`
- `src/Skoruba.Duende.IdentityServer.STS.Identity/wwwroot`

Examples:

- React admin UI behavior
- verify screen styling
- phone login tabs
- countdown or AJAX verify UX

### 9.2 Admin API Surface

Change:

- `src/Skoruba.Duende.IdentityServer.Admin.Api`
- `src/Skoruba.Duende.IdentityServer.Admin.UI.Api`

Examples:

- add or change REST endpoints
- public tenant/client endpoints
- UI-facing helper APIs

### 9.3 Business Rules

Change:

- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic*`

Examples:

- validation
- client/resource governance rules
- orchestration rules

### 9.4 Persistence

Change:

- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework*`

Examples:

- EF entities
- migrations
- provider configuration
- storage mapping

### 9.5 Authentication / Token Behavior

Change:

- `src/Skoruba.Duende.IdentityServer.STS.Identity`

Examples:

- login flows
- OTP integration
- session cookies
- continuation logic
- account selection
- user API integration

### 9.6 Tenant Infrastructure

Change:

- `src/Skoruba.Duende.IdentityServer.TenantInfrastructure`
- shared tenant-aware services in `STS.Identity` or shared configuration packages

Examples:

- tenant resolution
- tenant bootstrap
- public-safe tenant/client lookup

## 10. Summary

This repository uses a pragmatic enterprise architecture:

- layered design
- strong DI composition
- typed options
- adapters for external systems
- provider-based infrastructure swapping
- protected transient state
- feature flags
- tenant-aware auth continuation

The most important current custom identity feature is the phone OTP flow integrated with an external `UserApi`. That flow is intentionally designed to preserve `Duende IdentityServer` continuation semantics while delegating OTP issuance and verification to a separate service.

When extending the system, the safest approach is:

- keep auth continuation inside the STS
- keep external integrations behind adapters
- preserve tenant boundaries
- keep UI concerns out of controllers where possible
- prefer typed contracts and small blast radius changes
