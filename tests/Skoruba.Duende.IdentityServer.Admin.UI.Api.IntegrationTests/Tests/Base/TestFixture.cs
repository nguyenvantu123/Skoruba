using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Services.Interfaces;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Configuration.Constants;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Controllers;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Common;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.IntegrationTests.Tests.Base
{
    public class TestFixture : IDisposable
    {
        public IHost Host { get; }
        public TestServer TestServer { get; }
        public HttpClient Client { get; }

        public TestFixture()
        {
            Host = new HostBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services
                            .AddAuthentication(TestAuthenticationHandler.Scheme)
                            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.Scheme, _ => { });

                        services.AddAuthorization(options =>
                        {
                            options.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthenticationHandler.Scheme)
                                .RequireAuthenticatedUser()
                                .Build();

                            options.AddPolicy(AuthorizationConsts.AdministrationPolicy, policy =>
                            {
                                policy.AuthenticationSchemes.Add(TestAuthenticationHandler.Scheme);
                                policy.RequireAuthenticatedUser();
                            });

                            options.AddPolicy(AuthorizationConsts.SuperAdminPolicy, policy =>
                            {
                                policy.AuthenticationSchemes.Add(TestAuthenticationHandler.Scheme);
                                policy.RequireAuthenticatedUser();
                            });
                        });

                        services.Configure<PublicTenantDirectoryConfiguration>(options =>
                        {
                            options.ResponseCacheSeconds = 300;
                            options.SearchMinLength = 2;
                            options.SearchMaxLength = 100;
                            options.RateLimitPermitLimit = 30;
                            options.RateLimitWindowSeconds = 60;
                            options.RateLimitQueueLimit = 0;
                        });

                        services.AddResponseCaching();
                        services.AddRateLimiter(options =>
                        {
                            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                            options.AddPolicy(PublicTenantApiConsts.RateLimitPolicy, httpContext =>
                                RateLimitPartition.GetFixedWindowLimiter(
                                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "test",
                                    _ => new FixedWindowRateLimiterOptions
                                    {
                                        PermitLimit = 30,
                                        Window = TimeSpan.FromSeconds(60),
                                        QueueLimit = 0,
                                        AutoReplenishment = true
                                    }));
                        });

                        services.AddSingleton<ITenantService, TestTenantService>();
                        services.AddControllers()
                            .AddApplicationPart(typeof(TenantsController).Assembly);
                    });

                    webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseResponseCaching();
                        app.UseRateLimiter();
                        app.UseAuthentication();
                        app.UseAuthorization();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .Start();

            TestServer = Host.GetTestServer();
            Client = Host.GetTestClient();
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
        }
    }
}
