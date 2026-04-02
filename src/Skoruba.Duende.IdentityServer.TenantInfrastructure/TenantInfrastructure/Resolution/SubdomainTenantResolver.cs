using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace TenantInfrastructure.Resolution;

public interface ITenantResolver
{
    string? ResolveTenantKey(HttpContext http);
}

public sealed class SubdomainTenantResolver : ITenantResolver
{
    private readonly TenantResolutionOptions _opt;

    public SubdomainTenantResolver(TenantResolutionOptions opt) => _opt = opt;

    public string? ResolveTenantKey(HttpContext http)
    {
        var fromQuery = ResolveFromQuery(http);
        if (!string.IsNullOrWhiteSpace(fromQuery))
        {
            return fromQuery;
        }

        var fromReturnUrl = ResolveFromReturnUrl(http);
        if (!string.IsNullOrWhiteSpace(fromReturnUrl))
        {
            return fromReturnUrl;
        }

        var fromHeader = ResolveFromHeaders(http);
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return fromHeader;
        }

        var fromCookie = ResolveFromCookies(http);
        if (!string.IsNullOrWhiteSpace(fromCookie))
        {
            return fromCookie;
        }

        var host = http.Request.Host.Host;
        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < _opt.MinHostParts) return null;

        var raw = parts[0];
        var key = _opt.Normalize(raw);

        if (_opt.ReservedSubdomains.Contains(key)) return null;
        return key;
    }

    private string? ResolveFromQuery(HttpContext http)
    {
        foreach (var queryName in _opt.TenantQueryNames)
        {
            if (!http.Request.Query.TryGetValue(queryName, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            var key = Normalize(raw);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private string? ResolveFromReturnUrl(HttpContext http)
    {
        if (!TryGetReturnUrlRaw(http, out var rawReturnUrl))
        {
            return null;
        }

        foreach (var candidate in ExpandEncodedCandidates(rawReturnUrl))
        {
            var query = ExtractQueryPart(candidate);
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            var parsed = QueryHelpers.ParseQuery(query.StartsWith("?") ? query : $"?{query}");
            foreach (var tenantQueryName in _opt.TenantQueryNames)
            {
                if (!parsed.TryGetValue(tenantQueryName, out var values))
                {
                    continue;
                }

                var raw = values.FirstOrDefault();
                var key = Normalize(raw);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }
            }
        }

        return null;
    }

    private static bool TryGetReturnUrlRaw(HttpContext http, out string rawReturnUrl)
    {
        rawReturnUrl = string.Empty;
        if (http.Request.Query.TryGetValue("returnUrl", out var lowerValues))
        {
            rawReturnUrl = lowerValues.FirstOrDefault() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(rawReturnUrl);
        }

        if (http.Request.Query.TryGetValue("ReturnUrl", out var upperValues))
        {
            rawReturnUrl = upperValues.FirstOrDefault() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(rawReturnUrl);
        }

        return false;
    }

    private static IEnumerable<string> ExpandEncodedCandidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = raw;

        for (var i = 0; i < 3; i++)
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

    private string? ResolveFromHeaders(HttpContext http)
    {
        foreach (var headerName in _opt.TenantHeaderNames)
        {
            if (!http.Request.Headers.TryGetValue(headerName, out var values))
            {
                continue;
            }

            var raw = values.FirstOrDefault();
            var key = Normalize(raw);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private string? ResolveFromCookies(HttpContext http)
    {
        foreach (var cookieName in _opt.TenantCookieNames)
        {
            if (!http.Request.Cookies.TryGetValue(cookieName, out var raw))
            {
                continue;
            }

            var key = Normalize(raw);
            if (!string.IsNullOrWhiteSpace(key))
            {
                return key;
            }
        }

        return null;
    }

    private string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return _opt.Normalize(raw);
    }
}
