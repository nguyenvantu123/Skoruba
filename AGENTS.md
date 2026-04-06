# AGENTS.md

## Mission
You are working in a multi-layer .NET enterprise solution based on these architectural boundaries:

- Presentation/UI: React SPA + ASP.NET Core host
- API: ASP.NET Core REST API
- Business Logic: domain/application services
- Data Access: EF Core contexts, repositories, migrations
- STS: Duende IdentityServer host
- Shared: DTOs, configuration, tenant infrastructure

Your goal is to implement user requirements while preserving architecture, security boundaries, and maintainability.

## Hard rules
- Do not bypass architecture layers.
- Do not access DbContext directly from UI or controller code unless the existing pattern already does so.
- Prefer extending BusinessLogic services before adding logic in controllers.
- Prefer strongly typed DTOs over anonymous payloads.
- Keep code production-oriented, not demo-oriented.
- Preserve existing naming conventions and folder conventions.
- Minimize blast radius of changes.
- If a request involves writes that are risky, first create a plan in markdown before applying changes.

## Solution architecture map
- UI/UX changes: `src/Skoruba.Duende.IdentityServer.Admin.UI.Client`
- UI host/composition changes: `src/Skoruba.Duende.IdentityServer.Admin`, `src/Skoruba.Duende.IdentityServer.Admin.UI`, `src/Skoruba.Duende.IdentityServer.Admin.UI.Spa`
- API surface: `src/Skoruba.Duende.IdentityServer.Admin.Api` and `src/Skoruba.Duende.IdentityServer.Admin.UI.Api`
- Business rules: `src/Skoruba.Duende.IdentityServer.Admin.BusinessLogic*`
- Persistence: `src/Skoruba.Duende.IdentityServer.Admin.EntityFramework*`
- Auth/token behavior: `src/Skoruba.Duende.IdentityServer.STS.Identity`
- Shared contracts/config: `src/Skoruba.Duende.IdentityServer.Shared*`
- Tenant infrastructure: `src/Skoruba.Duende.IdentityServer.TenantInfrastructure`

## Working style
- First inspect relevant files before editing.
- Before major changes, summarize the implementation plan in 5-10 bullets.
- After edits, run the narrowest useful validation first, then broader tests if needed.
- For UI client changes, run `npm run lint` and `npm run build` in `src/Skoruba.Duende.IdentityServer.Admin.UI.Client` before marking done.
- When changing API contracts, check UI callers and DTO mappings.
- When changing EF entities, check mapping, migrations, and dependent services.
- When changing IdentityServer config or auth flow, review security implications explicitly.

## Output requirements
When finishing a task, always report:
1. What changed
2. Why it changed
3. Files changed
4. Validation commands run
5. Risks / follow-up items

## Preferred commands
- Restore: `dotnet restore`
- Build solution: `dotnet build`
- Run focused build: `dotnet build <project-path>`
- Test: `dotnet test`
- Frontend install: `npm install`
- Frontend clean install (CI-style): `npm ci`
- Frontend lint: `npm run lint`
- Frontend build: `npm run build`
- Frontend test/lint if present: inspect package.json first

## Safety
- Never delete broad folders unless explicitly requested.
- Never rotate secrets or credentials unless explicitly requested.
- Never change auth/token lifetimes silently.
- Never generate fake migrations without checking actual model changes.