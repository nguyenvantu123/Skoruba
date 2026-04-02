using System.Collections.Generic;
using System.Net;

namespace Skoruba.Duende.IdentityServer.Admin.Api.Helpers;

public static class HostMatcher
{
    public static bool IsAllowed(string host, IEnumerable<string> allowedHosts)
    {
        var normalizedHost = host.Trim().ToLowerInvariant();

        foreach (var allowed in allowedHosts)
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;

            var normalizedAllowed = allowed.Trim().ToLowerInvariant();

            if (normalizedAllowed == "*") return true;

            if (string.Equals(normalizedHost, normalizedAllowed, StringComparison.OrdinalIgnoreCase))
                return true;

            if (AreEquivalentLoopbackHosts(normalizedHost, normalizedAllowed))
                return true;

            if (normalizedAllowed.StartsWith("*."))
            {
                var suffix = normalizedAllowed[1..];
                if (normalizedHost.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    normalizedHost.Length > suffix.Length)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool AreEquivalentLoopbackHosts(string left, string right)
    {
        return IsLoopbackHost(left) && IsLoopbackHost(right);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ipAddress) && IPAddress.IsLoopback(ipAddress);
    }
}
