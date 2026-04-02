// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Identity;
using TenantInfrastructure.Identity;

namespace Skoruba.Duende.IdentityServer.Admin.EntityFramework.Shared.Entities.Identity
{
    public class UserIdentity : IdentityUser
    {
        public string TenantKey { get; set; } = string.Empty;
        public string BranchCode { get; set; } = string.Empty;
    }
}