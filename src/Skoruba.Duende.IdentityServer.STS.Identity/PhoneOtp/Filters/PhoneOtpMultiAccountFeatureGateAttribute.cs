using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Configuration;

namespace Skoruba.Duende.IdentityServer.STS.Identity.PhoneOtp.Filters;

/// <summary>
/// Action-level feature gate cho nhánh multi-account-select. Trả về
/// <see cref="NotFoundResult"/> khi <c>PhoneOtpLogin:Enabled = false</c> hoặc
/// <c>PhoneOtpLogin:MultiAccount:Enabled = false</c>. Áp dụng cho route
/// <c>/Account/LoginWithPhone/SelectAccount</c> (Requirements 1.2, 1.8, 14.4).
/// </summary>
/// <remarks>
/// Filter intentionally không log để route 404 trông giống endpoint không tồn tại
/// (anti-enumeration, Section 14 design).
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class PhoneOtpMultiAccountFeatureGateAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<PhoneOtpLoginConfiguration>>();

        var config = options.Value;

        if (!config.Enabled || !config.MultiAccount.Enabled)
        {
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
