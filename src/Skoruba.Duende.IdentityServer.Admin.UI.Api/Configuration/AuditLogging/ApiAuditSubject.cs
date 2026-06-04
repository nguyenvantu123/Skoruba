// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Skoruba.AuditLogging.Constants;
using Skoruba.AuditLogging.Events;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.AuditLogging
{
    public class ApiAuditSubject : IAuditSubject
    {
        public ApiAuditSubject(IHttpContextAccessor accessor, AuditLoggingConfiguration auditLoggingConfiguration)
        {
            var httpContext = accessor.HttpContext;
            var user = httpContext?.User;

            var subClaim = FindClaim(user, auditLoggingConfiguration.SubjectIdentifierClaim);
            var nameClaim = FindClaim(user, auditLoggingConfiguration.SubjectNameClaim);
            var clientIdClaim = FindClaim(user, auditLoggingConfiguration.ClientIdClaim);
            var fallbackSubject = clientIdClaim?.Value ?? "system";
            var subjectIdentifier = subClaim?.Value ?? fallbackSubject;

            SubjectIdentifier = subjectIdentifier;
            SubjectName = subClaim == null
                ? fallbackSubject
                : nameClaim?.Value ?? subjectIdentifier;
            SubjectType = subClaim == null ? AuditSubjectTypes.Machine : AuditSubjectTypes.User;

            SubjectAdditionalData = new
            {
                RemoteIpAddress = httpContext?.Connection?.RemoteIpAddress?.ToString(),
                LocalIpAddress = httpContext?.Connection?.LocalIpAddress?.ToString(),
                Claims = user?.Claims?.Select(x => new { x.Type, x.Value }).ToArray()
            };
        }

        public string SubjectName { get; set; }

        public string SubjectType { get; set; }

        public object SubjectAdditionalData { get; set; }

        public string SubjectIdentifier { get; set; }

        private static Claim? FindClaim(ClaimsPrincipal? principal, string? claimType)
        {
            return string.IsNullOrWhiteSpace(claimType)
                ? null
                : principal?.FindFirst(claimType);
        }
    }
}
