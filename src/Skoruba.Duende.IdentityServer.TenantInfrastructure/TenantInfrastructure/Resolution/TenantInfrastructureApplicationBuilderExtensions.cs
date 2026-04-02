using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Text;

namespace TenantInfrastructure.Resolution
{
    public static class TenantInfrastructureApplicationBuilderExtensions
    {
        public static IApplicationBuilder UseTenantInfrastructure(this IApplicationBuilder app)
            => app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
