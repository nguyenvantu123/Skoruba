using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using Duende.AccessTokenManagement.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Skoruba.Duende.IdentityServer.Admin.UI.Services.AntiForgeryProtection;
using Skoruba.Duende.IdentityServer.Admin.UI.Services.Configurations;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Services;

public static class IdentityServices
{
    public static string? GetUserId(this ClaimsPrincipal claimsPrincipal) => ((ClaimsIdentity)claimsPrincipal.Identity!).FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;

    public static string? GetUserEmail(this ClaimsPrincipal claimsPrincipal) => ((ClaimsIdentity)claimsPrincipal.Identity!).FindFirst(JwtRegisteredClaimNames.Email)?.Value;
    
    public static string? GetUserName(this ClaimsPrincipal claimsPrincipal) => ((ClaimsIdentity)claimsPrincipal.Identity!).FindFirst(JwtRegisteredClaimNames.Name)?.Value;
    
    public static void UseAntiForgeryProtection(this WebApplication app)
    {
        app.UseMiddleware<AntiForgeryProtectionMiddleware>();
    }
    
    public static void AddRemoteApiProxy(this IServiceCollection services, RemoteApiConfiguration? config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        var loopbackDevHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var api in config.Apis)
        {
            if (Uri.TryCreate(api.RemoteUrl, UriKind.Absolute, out var remoteUri) &&
                remoteUri.Host.EndsWith(".dev.localhost", StringComparison.OrdinalIgnoreCase))
            {
                loopbackDevHosts.Add(remoteUri.Host);
            }

            var route = new RouteConfig
            {
                RouteId = api.ApiName,
                ClusterId = api.ApiName,
                Match = new RouteMatch { Path = $"/{api.ApiName}/{{**catch-all}}" },
                Metadata = new Dictionary<string, string>
                {
                    { nameof(AntiForgeryProtectionAttribute), api.UseCsrfProtection.ToString() },
                }
            };

            route = route.WithTransformPathRemovePrefix(prefix: $"/{api.ApiName}");

            routes.Add(route);

            clusters.Add(new ClusterConfig
            {
                ClusterId = api.ApiName,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    { api.ApiName, new DestinationConfig { Address = api.RemoteUrl } }
                }
            });
        }

        var reverseProxyBuilder = services.AddReverseProxy().LoadFromMemory(routes, clusters);

        if (loopbackDevHosts.Count > 0)
        {
            reverseProxyBuilder.ConfigureHttpClient((_, handler) =>
            {
                if (handler is SocketsHttpHandler socketsHttpHandler)
                {
                    // .NET on Windows may not resolve custom .dev.localhost names even though the browser can.
                    // For local dev proxy hops, connect to loopback directly and keep the original HTTPS host for TLS.
                    socketsHttpHandler.ConnectCallback = (context, cancellationToken) =>
                        ConnectToProxyDestinationAsync(context, cancellationToken, loopbackDevHosts);
                }
            });
        }
    }

    public static void AddRemoteApisProxy(this IEndpointRouteBuilder route)
    {
        route.MapReverseProxy(proxyPipeline =>
        {
            proxyPipeline.Use(async (context, next) =>
            {
                if (VerifyAntiForgeryProtection(context))
                {
                    return;
                }

                var tokenResult = await context.GetUserAccessTokenAsync();
                var accessToken = tokenResult.Succeeded ? tokenResult.Token?.AccessToken.ToString() : null;
                if (!string.IsNullOrEmpty(accessToken))
                {
                    context.Request.Headers.Authorization = $"Bearer {accessToken}";
                }

                await next();
            });
        });
    }

    private static bool VerifyAntiForgeryProtection(HttpContext context)
    {
        var routeModel = context.GetRouteModel();
        var metadata = routeModel.Config.Metadata;

        var requiresCsrf = metadata != null &&
                           metadata.TryGetValue(nameof(AntiForgeryProtectionAttribute), out var value) &&
                           bool.TryParse(value, out var enabled) &&
                           enabled;

        if (!requiresCsrf)
        {
            return false;
        }

        if (!RequiresAntiForgeryValidation(context))
        {
            return false;
        }

        if (context.Request.Headers.TryGetValue(AntiForgeryProtectionConsts.AntiForgeryHeader, out var csrfValue) &&
            csrfValue == AntiForgeryProtectionConsts.AntiForgeryHeaderValue)
        {
            return false;
        }
        
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        
        return true;
    }

    private static bool RequiresAntiForgeryValidation(HttpContext context)
    {
        var method = context.Request.Method;

        return !HttpMethods.IsGet(method) &&
               !HttpMethods.IsHead(method) &&
               !HttpMethods.IsOptions(method) &&
               !HttpMethods.IsTrace(method);
    }

    private static async ValueTask<Stream> ConnectToProxyDestinationAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken,
        IReadOnlySet<string> loopbackDevHosts)
    {
        var dnsEndPoint = context.DnsEndPoint;

        if (loopbackDevHosts.Contains(dnsEndPoint.Host))
        {
            return await ConnectAsync(IPAddress.Loopback, dnsEndPoint.Port, cancellationToken);
        }

        var addresses = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken);
        Exception? lastException = null;

        foreach (var address in addresses)
        {
            try
            {
                return await ConnectAsync(address, dnsEndPoint.Port, cancellationToken);
            }
            catch (SocketException exception)
            {
                lastException = exception;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostNotFound);
    }

    private static async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
