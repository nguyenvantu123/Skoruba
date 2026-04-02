# Architecture

This document explains the major layers in the solution, what each layer is responsible for, and how requests flow through the system.

## System Overview

At runtime the solution runs three primary services:

- `Skoruba.Duende.IdentityServer.STS.Identity`: IdentityServer (STS) for authentication and token issuance.
- `Skoruba.Duende.IdentityServer.Admin.Api`: REST API used by the admin UI to manage IdentityServer and Identity data.
- `Skoruba.Duende.IdentityServer.Admin`: Admin UI host (ASP.NET Core) that serves the SPA and handles OIDC login.

The UI is built as a React SPA and communicates with the Admin API. Authentication is performed against the STS.

## Layers And Responsibilities

### 1) Presentation Layer (UI)

Responsible for user experience, navigation, and calling the Admin API.

- `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
  - React 18 + TypeScript + Tailwind + shadcn/ui SPA.
  - Owns UI state, routing, forms, and API client calls.
- `src/Skoruba.Duende.IdentityServer.Admin.UI.Spa`
  - Static SPA host output (wwwroot), used when the SPA is built and deployed.
- `src/Skoruba.Duende.IdentityServer.Admin`
  - ASP.NET Core host for the UI.
  - Manages OIDC sign-in/out with the STS and serves the SPA.
- `src/Skoruba.Duende.IdentityServer.Admin.UI`
  - UI-specific services and composition for the host.

### 2) API Layer (HTTP / REST)

Responsible for external HTTP endpoints, input validation, and mapping to business logic.

- `src/Skoruba.Duende.IdentityServer.Admin.Api`
  - Main REST API consumed by the UI.
  - Hosts API endpoints for clients, resources, identity, and admin workflows.
- `src/Skoruba.Duende.IdentityServer.Admin.UI.Api`
  - UI-facing API surface and helpers used by the admin host.
  - Provides controllers, DTOs, mapping, and middleware for UI-related API interactions.

### 3) Business Logic Layer

Responsible for domain rules, workflows, validation, and orchestrating data access.

- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic`
  - Core admin business services, configuration rules, DTOs, events, and mapping.
- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Identity`
  - Identity-specific services and rules.
- `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Shared`
  - Shared services and cross-cutting logic used by multiple business modules.

### 4) Data Access Layer (Entity Framework)

Responsible for persistence, EF Core contexts, repositories, and migrations.

- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin`
  - Admin domain EF Core DbContexts and repositories.
- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Admin.Storage`
  - Storage entities, interfaces, DTOs, and mapping helpers for admin data.
- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Configuration`
  - EF Core store for IdentityServer configuration data.
- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Identity`
  - EF Core store for ASP.NET Core Identity data.
- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared`
  - Shared EF Core utilities used by multiple providers.
- `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.Extensions`
  - Extension helpers for EF Core configuration and setup.
- Provider-specific EF packages:
  - `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.SqlServer`
  - `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.PostgreSQL`
  - `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework.MySql`

### 5) Shared Contracts And Configuration

Responsible for shared DTOs, configuration helpers, and cross-cutting services.

- `src/Skoruba.Duende.IdentityServer.Shared`
  - Shared DTOs used across services and layers.
- `src/Skoruba.Duende.IdentityServer.Shared.Configuration`
  - Shared configuration models, authentication helpers, email configuration, and service wiring.
- `src/Skoruba.Duende.IdentityServer.TenantInfrastructure`
  - Tenant-related infrastructure and helpers for multi-tenant setups.

### 6) Security Token Service (STS)

Responsible for authentication, token issuance, and identity management.

- `src/Skoruba.Duende.IdentityServer.STS.Identity`
  - Duende IdentityServer host and ASP.NET Core Identity integration.

## Typical Request Flow

1. User accesses the Admin UI (ASP.NET Core host) and logs in via the STS.
2. The SPA calls the Admin API with an access token.
3. The Admin API validates the request and calls business services.
4. Business services perform validation and orchestrate EF Core repositories.
5. EF Core reads/writes to the configured database provider.
6. Results are returned to the UI and rendered in the SPA.

## Where To Look For Changes

- UI and UX changes: `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
- API surface changes: `src/Skoruba.Duende.IdentityServer.Admin.Api` and `src/Skoruba.Duende.IdentityServer.Admin.UI.Api`
- Business logic or rules: `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic*`
- Persistence or migrations: `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework*`
- Auth and token behavior: `src/Skoruba.Duende.IdentityServer.STS.Identity`
