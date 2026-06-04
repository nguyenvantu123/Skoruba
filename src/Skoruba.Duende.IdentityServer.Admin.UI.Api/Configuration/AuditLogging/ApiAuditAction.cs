// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Skoruba.AuditLogging.Events;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.AuditLogging
{
    public class ApiAuditAction : IAuditAction
    {
        public ApiAuditAction(IHttpContextAccessor accessor)
        {
            var httpContext = accessor.HttpContext;

            Action = new
            {
                TraceIdentifier = httpContext?.TraceIdentifier,
                RequestUrl = httpContext?.Request?.GetDisplayUrl(),
                HttpMethod = httpContext?.Request?.Method,
                IsHttpRequest = httpContext != null
            };
        }

        public object Action { get; set; }
    }
}
