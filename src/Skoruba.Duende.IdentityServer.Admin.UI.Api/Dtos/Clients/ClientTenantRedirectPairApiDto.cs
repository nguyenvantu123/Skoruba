// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Clients
{
    public class ClientTenantRedirectPairApiDto
    {
        public string TenantKey { get; set; }
        public string SignInCallbackUrl { get; set; }
        public string SignOutCallbackUrl { get; set; }
        public string CorsOrigin { get; set; }
    }
}
