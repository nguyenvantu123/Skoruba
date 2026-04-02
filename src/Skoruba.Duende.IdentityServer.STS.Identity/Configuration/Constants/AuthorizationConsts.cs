// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

namespace Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants
{
    public class AuthorizationConsts
    {
        public const string AdministrationPolicy = "RequireAdministratorRole";
        public const string TenantAdminApiPolicy = "RequireTenantAdminApiAccess";
        public const string TenantAdminTenantRegistryPolicy = "RequireTenantAdminTenantRegistryAccess";
    }
}