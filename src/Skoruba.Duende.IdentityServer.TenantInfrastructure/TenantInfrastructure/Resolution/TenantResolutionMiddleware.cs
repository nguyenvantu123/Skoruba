using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using TenantInfrastructure.Abstractions;
using TenantInfrastructure.MasterDb;

namespace TenantInfrastructure.Resolution;

public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ITenantResolver _resolver;
    private readonly TenantResolutionOptions _opt;

    public TenantResolutionMiddleware(RequestDelegate next, TenantResolutionOptions opt)
    {
        _next = next;
        _resolver = new SubdomainTenantResolver(opt);

        _opt = opt;
    }

    public async Task Invoke(
        HttpContext http,
        ITenantStore store,
        ITenantRepository repository,
        ITenantContextAccessor accessor)
    {
        var host = http.Request.Host.Host;

        var tenantKey = _resolver.ResolveTenantKey(http);
        tenantKey ??= await ResolveTenantKeyFromRequestPayloadAsync(http, repository);

        if (string.IsNullOrWhiteSpace(tenantKey) &&
            !string.IsNullOrWhiteSpace(host) &&
            _opt.SkipHosts.Contains(host))
        {
            await _next(http);
            return;
        }

        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            if (_opt.AllowMissingTenant)
            {
                await _next(http);
                return;
            }

            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("Missing tenant identifier.");
            return;
        }

        var tenant = await store.FindAsync(tenantKey, http.RequestAborted);
        if (tenant is null || !tenant.IsActive)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsync("Tenant not found.");
            return;
        }

        PersistTenantCookie(http, tenant.TenantKey);

        accessor.Set(new TenantContext(
            TenantKey: tenant.TenantKey,
            ConnectionSecrets: tenant.ConnectionSecrets));

        try
        {
            await _next(http);
        }
        finally
        {
            accessor.Clear();
        }
    }

    private async Task<string?> ResolveTenantKeyFromRequestPayloadAsync(HttpContext http, ITenantRepository repository)
    {
        var form = await TryReadFormAsync(http);

        var directTenantKey = TryResolveTenantKeyFromCollection(http.Request.Query);
        if (!string.IsNullOrWhiteSpace(directTenantKey))
        {
            return directTenantKey;
        }

        directTenantKey = TryResolveTenantKeyFromCollection(form);
        if (!string.IsNullOrWhiteSpace(directTenantKey))
        {
            return directTenantKey;
        }

        foreach (var rawValue in EnumerateRequestValues(http, form))
        {
            foreach (var candidate in ExpandEncodedCandidates(rawValue))
            {
                foreach (var queryCandidate in ExtractQueryCandidates(candidate))
                {
                    var parsed = QueryHelpers.ParseQuery(queryCandidate.StartsWith("?") ? queryCandidate : $"?{queryCandidate}");

                    directTenantKey = TryResolveTenantKeyFromCollection(parsed);
                    if (!string.IsNullOrWhiteSpace(directTenantKey))
                    {
                        return directTenantKey;
                    }

                    foreach (var absoluteUrl in ExtractAbsoluteUrlValues(parsed))
                    {
                        var matchedTenantKey = await ResolveTenantKeyFromRedirectUrlAsync(absoluteUrl, repository, http.RequestAborted);
                        if (!string.IsNullOrWhiteSpace(matchedTenantKey))
                        {
                            return matchedTenantKey;
                        }
                    }
                }

                var directMatch = await ResolveTenantKeyFromRedirectUrlAsync(candidate, repository, http.RequestAborted);
                if (!string.IsNullOrWhiteSpace(directMatch))
                {
                    return directMatch;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRequestValues(
        HttpContext http,
        IFormCollection? form)
    {
        foreach (var pair in http.Request.Query)
        {
            foreach (var value in pair.Value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }

        if (form == null)
        {
            yield break;
        }

        foreach (var pair in form)
        {
            foreach (var value in pair.Value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<string> ExtractAbsoluteUrlValues(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> parsed)
    {
        foreach (var pair in parsed)
        {
            foreach (var rawValue in pair.Value)
            {
                foreach (var candidate in ExpandEncodedCandidates(rawValue))
                {
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    {
                        yield return candidate;
                    }
                }
            }
        }
    }

    private async Task<string?> ResolveTenantKeyFromRedirectUrlAsync(
        string candidate,
        ITenantRepository repository,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var requestedUri) ||
            (requestedUri.Scheme != Uri.UriSchemeHttp && requestedUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var tenantKeyFromHost = TryResolveTenantKeyFromHost(requestedUri.Host);
        if (!string.IsNullOrWhiteSpace(tenantKeyFromHost))
        {
            return tenantKeyFromHost;
        }

        var tenants = await repository.GetTenantsAsync(null, ct);
        foreach (var tenant in tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.RedirectUrl) ||
                !Uri.TryCreate(tenant.RedirectUrl, UriKind.Absolute, out var tenantUri))
            {
                continue;
            }

            if (!string.Equals(requestedUri.Scheme, tenantUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requestedUri.Host, tenantUri.Host, StringComparison.OrdinalIgnoreCase) ||
                requestedUri.Port != tenantUri.Port)
            {
                continue;
            }

            var tenantBasePath = NormalizePathPrefix(tenantUri.AbsolutePath);
            var requestedPath = NormalizePathPrefix(requestedUri.AbsolutePath);
            if (requestedPath.StartsWith(tenantBasePath, StringComparison.OrdinalIgnoreCase))
            {
                return Normalize(tenant.TenantKey);
            }
        }

        return null;
    }

    private string? TryResolveTenantKeyFromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < _opt.MinHostParts)
        {
            return null;
        }

        var raw = parts[0];
        var key = Normalize(raw);
        if (string.IsNullOrWhiteSpace(key) || _opt.ReservedSubdomains.Contains(key))
        {
            return null;
        }

        return key;
    }

    private static IEnumerable<string> ExtractQueryCandidates(string url)
    {
        var query = ExtractQueryPart(url);
        if (!string.IsNullOrWhiteSpace(query))
        {
            yield return query;
        }
    }

    private string? TryResolveTenantKeyFromCollection(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>? values)
    {
        if (values == null)
        {
            return null;
        }

        foreach (var tenantQueryName in _opt.TenantQueryNames)
        {
            var match = values.FirstOrDefault(x => string.Equals(x.Key, tenantQueryName, StringComparison.OrdinalIgnoreCase));
            var key = Normalize(match.Value.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private static async Task<IFormCollection?> TryReadFormAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType)
        {
            return null;
        }

        return await http.Request.ReadFormAsync(http.RequestAborted);
    }

    private static IEnumerable<string> ExpandEncodedCandidates(string? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = raw;

        for (var i = 0; i < 4; i++)
        {
            if (string.IsNullOrWhiteSpace(current) || !seen.Add(current))
            {
                yield break;
            }

            yield return current;

            var decoded = Uri.UnescapeDataString(current);
            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                yield break;
            }

            current = decoded;
        }
    }

    private static string? ExtractQueryPart(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var index = url.IndexOf('?', StringComparison.Ordinal);
        if (index < 0 || index == url.Length - 1)
        {
            return null;
        }

        return url[(index + 1)..];
    }

    private string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return _opt.Normalize(raw);
    }

    private static string NormalizePathPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        return path.EndsWith("/", StringComparison.Ordinal) ? path : $"{path}/";
    }

    private static void PersistTenantCookie(HttpContext http, string tenantKey)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return;
        }

        if (http.Request.Cookies.TryGetValue("tenant", out var existing) &&
            string.Equals(existing, tenantKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        http.Response.Cookies.Append("tenant", tenantKey, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(30)
        });
    }
}
