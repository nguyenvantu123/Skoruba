Review and implement this multi-tenant requirement safely:

{{requirement}}

Important:
- inspect TenantInfrastructure first
- preserve tenant isolation
- check API, BusinessLogic, queries, cache keys, DTOs, and UI assumptions
- prefer least-privilege behavior if the requirement is ambiguous

Return:
- tenant-safety review
- implementation plan
- files changed
- validation performed
- isolation risks