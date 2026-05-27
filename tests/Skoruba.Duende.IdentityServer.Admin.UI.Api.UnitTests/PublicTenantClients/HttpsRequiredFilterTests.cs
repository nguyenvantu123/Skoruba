// Feature: tenant-client-cache-public-read, Task 3
//
// Example-based tests for HttpsRequiredFilter covering:
//   R9.7 — non-HTTPS, non-loopback request rejected with 400 https_required.
//   R9.7 — HTTPS request allowed (filter no-ops, ctx.Result remains null).
//   R9.7 — http://localhost AND http loopback IP allowed (developer flow).

#nullable enable

using System.Net;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

using Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

using Xunit;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.UnitTests.PublicTenantClients;

public class HttpsRequiredFilterTests
{
    private static AuthorizationFilterContext BuildContext(
        bool isHttps,
        string? hostHeader,
        IPAddress? remoteIp)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = isHttps ? "https" : "http";
        if (hostHeader is not null)
        {
            http.Request.Host = new HostString(hostHeader);
        }

        if (remoteIp is not null)
        {
            http.Connection.RemoteIpAddress = remoteIp;
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new IFilterMetadata[0]);
    }

    [Fact]
    public async Task Https_Request_Allowed_NoResultSet()
    {
        var ctx = BuildContext(isHttps: true, hostHeader: "example.com", remoteIp: IPAddress.Parse("203.0.113.10"));

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull("HTTPS requests must fall through to subsequent filters");
    }

    [Fact]
    public async Task Http_NonLoopback_Returns_400_HttpsRequired()
    {
        var ctx = BuildContext(isHttps: false, hostHeader: "example.com", remoteIp: IPAddress.Parse("203.0.113.10"));

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<ObjectResult>();
        var result = (ObjectResult)ctx.Result!;
        result.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        result.ContentTypes.Should().Contain("application/json; charset=utf-8");
        result.Value.Should().BeEquivalentTo(new { error = "https_required" });
    }

    [Fact]
    public async Task Http_Localhost_HostHeader_Allowed()
    {
        // Developer flow: dotnet watch / kestrel without HTTPS cert, host
        // header equals "localhost". We bypass the filter even when the
        // remote IP is not yet populated (in-process tests).
        var ctx = BuildContext(isHttps: false, hostHeader: "localhost", remoteIp: null);

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Http_Localhost_HostHeader_CaseInsensitive_Allowed()
    {
        var ctx = BuildContext(isHttps: false, hostHeader: "LOCALHOST", remoteIp: null);

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Http_Loopback_RemoteIp_IPv4_Allowed()
    {
        // Reverse proxy at 127.0.0.1 with arbitrary host header — still a
        // local developer flow.
        var ctx = BuildContext(isHttps: false, hostHeader: "intranet.local", remoteIp: IPAddress.Parse("127.0.0.1"));

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Http_Loopback_RemoteIp_IPv6_Allowed()
    {
        var ctx = BuildContext(isHttps: false, hostHeader: "intranet.local", remoteIp: IPAddress.IPv6Loopback);

        await new HttpsRequiredFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }
}
