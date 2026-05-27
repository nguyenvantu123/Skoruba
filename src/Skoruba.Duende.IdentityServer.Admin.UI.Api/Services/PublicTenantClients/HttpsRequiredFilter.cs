// Copyright (c) Jan Škoruba. All Rights Reserved.
// Licensed under the Apache License, Version 2.0.

#nullable enable

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Services.PublicTenantClients;

/// <summary>
/// Authorization filter that rejects non-HTTPS requests to the public-read
/// endpoint with HTTP 400 <c>{"error":"https_required"}</c>. Loopback
/// (<c>localhost</c> hostname or any
/// <see cref="IPAddress.IsLoopback(IPAddress)"/> remote address) is allowed
/// over plain HTTP so local developer flows continue to work without
/// certificates.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <c>Singleton</c>. The filter is stateless and resolves no
/// scoped collaborators. It runs as the FIRST filter on the controller
/// pipeline (R9.7) so an attacker cannot reach the API-key validation step
/// over plaintext HTTP. The implementation uses 400 (not 403 / 426) per
/// requirement R9.7.
/// </para>
/// </remarks>
internal sealed class HttpsRequiredFilter : IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = context.HttpContext.Request;
        if (request.IsHttps)
        {
            return Task.CompletedTask;
        }

        if (IsLoopback(request, context.HttpContext.Connection.RemoteIpAddress))
        {
            return Task.CompletedTask;
        }

        context.Result = new ObjectResult(new { error = "https_required" })
        {
            StatusCode = StatusCodes.Status400BadRequest,
            ContentTypes = { "application/json; charset=utf-8" },
        };

        return Task.CompletedTask;
    }

    /// <summary>
    /// Treat the request as loopback when either (a) the Host header equals
    /// <c>localhost</c> (case-insensitive) — which covers most developer
    /// loopback flows including reverse-proxy front-ends — or (b) the
    /// underlying TCP remote address is a loopback IP per
    /// <see cref="IPAddress.IsLoopback(IPAddress)"/>.
    /// </summary>
    private static bool IsLoopback(HttpRequest request, IPAddress? remoteIp)
    {
        var host = request.Host.HasValue ? request.Host.Host : null;
        if (!string.IsNullOrEmpty(host)
            && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (remoteIp is not null && IPAddress.IsLoopback(remoteIp))
        {
            return true;
        }

        return false;
    }
}
