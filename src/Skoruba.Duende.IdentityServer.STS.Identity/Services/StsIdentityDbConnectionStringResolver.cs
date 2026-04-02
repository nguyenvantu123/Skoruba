using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration.Constants;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using TenantInfrastructure.Abstractions;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Services;

public interface IStsIdentityDbConnectionStringResolver
{
    string ResolveConnectionString();
}

public sealed class StsIdentityDbConnectionStringResolver : IStsIdentityDbConnectionStringResolver
{
    private const string DefaultTenantTemplateConnectionStringName = "DefaultTenantConnection";
    private readonly IConfiguration _configuration;
    private readonly ITenantContextAccessor _tenantContextAccessor;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<TenantIdentityDbResolutionConfiguration> _options;
    private readonly ILogger<StsIdentityDbConnectionStringResolver> _logger;

    public StsIdentityDbConnectionStringResolver(
        IConfiguration configuration,
        ITenantContextAccessor tenantContextAccessor,
        IHttpContextAccessor httpContextAccessor,
        IOptions<TenantIdentityDbResolutionConfiguration> options,
        ILogger<StsIdentityDbConnectionStringResolver> logger)
    {
        _configuration = configuration;
        _tenantContextAccessor = tenantContextAccessor;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
        _logger = logger;
    }

    public string ResolveConnectionString()
    {
        var centralConnectionString = _configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);
        if (string.IsNullOrWhiteSpace(centralConnectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConfigurationConsts.IdentityDbConnectionStringKey}' is missing for STS identity database resolution.");
        }

        var tenantContext = _tenantContextAccessor.Current;
        if (tenantContext != null)
        {
            return ResolveTenantConnectionString(tenantContext);
        }

        if (IsCentralRequest())
        {
            _logger.LogDebug("Using central STS identity database for the current request because no tenant context was resolved.");
            return centralConnectionString;
        }

        throw new InvalidOperationException(
            "Tenant identity database resolution requires a resolved tenant context for non-central STS hosts.");
    }

    private string ResolveTenantConnectionString(TenantContext tenantContext)
    {
        // Tenant-scoped authorize/login requests can still arrive on the central STS host.
        // Once tenant context has been resolved from the request, it must win over host-based
        // central detection so the sign-in flow reads users from the correct tenant store.
        var serviceKey = NormalizeServiceKey(_options.Value.TenantIdentityServiceKey);
        var secretReference = tenantContext.GetConnectionSecret(serviceKey);
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantContext.TenantKey}' does not define a connection string for service key '{serviceKey}' in Tenant.ConnectionSecrets.");
        }

        var tenantConnectionString = ResolveTenantSecretReference(
            tenantContext.TenantKey,
            serviceKey,
            secretReference.Trim());

        if (string.IsNullOrWhiteSpace(tenantConnectionString))
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantContext.TenantKey}' secret '{secretReference}' for service key '{serviceKey}' could not be resolved to a usable connection string.");
        }

        _logger.LogInformation(
            "Using tenant-scoped STS identity database for tenant '{TenantKey}' and service key '{ServiceKey}'.",
            tenantContext.TenantKey,
            serviceKey);

        return tenantConnectionString.Trim();
    }

    private string? ResolveTenantSecretReference(string tenantKey, string serviceKey, string secretReference)
    {
        if (LooksLikeConnectionString(secretReference))
        {
            return secretReference;
        }

        foreach (var candidateKey in GetConfigurationLookupKeys(tenantKey, serviceKey, secretReference))
        {
            var configuredValue = _configuration[candidateKey];
            if (!string.IsNullOrWhiteSpace(configuredValue))
            {
                _logger.LogInformation(
                    "Resolved tenant DB secret '{SecretReference}' for tenant '{TenantKey}' from configuration key '{ConfigurationKey}'.",
                    secretReference,
                    tenantKey,
                    candidateKey);

                return configuredValue.Trim();
            }
        }

        var configuredConnectionString = _configuration.GetConnectionString(secretReference);
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            _logger.LogInformation(
                "Resolved tenant DB secret '{SecretReference}' for tenant '{TenantKey}' from ConnectionStrings section.",
                secretReference,
                tenantKey);

            return configuredConnectionString.Trim();
        }

        var developmentConnectionString = TryBuildDevelopmentTenantConnectionString(tenantKey, serviceKey, secretReference);
        if (!string.IsNullOrWhiteSpace(developmentConnectionString))
        {
            return developmentConnectionString;
        }

        return null;
    }

    private IEnumerable<string> GetConfigurationLookupKeys(string tenantKey, string serviceKey, string secretReference)
    {
        var normalizedSecretReference = secretReference.Trim().Trim('/');
        if (string.IsNullOrWhiteSpace(normalizedSecretReference))
        {
            yield break;
        }

        foreach (var candidate in new[]
        {
            normalizedSecretReference,
            normalizedSecretReference.Replace("/", ":"),
            normalizedSecretReference.Replace("/", "__"),
            $"TenantSecrets:{normalizedSecretReference}",
            $"TenantSecrets:{normalizedSecretReference.Replace("/", ":")}",
            $"TenantSecrets:{tenantKey}:{serviceKey}",
            $"TenantConnectionStrings:{tenantKey}:{serviceKey}",
            $"TenantConnectionStrings:{tenantKey}",
            $"ConnectionStrings:{normalizedSecretReference}",
            $"ConnectionStrings:{normalizedSecretReference.Replace("/", ":")}"
        }.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return candidate;
        }
    }

    private string? TryBuildDevelopmentTenantConnectionString(string tenantKey, string serviceKey, string secretReference)
    {
        if (!IsDevelopment())
        {
            return null;
        }

        var tenantDatabaseName = TryResolveDevelopmentDatabaseName(tenantKey, serviceKey, secretReference);
        if (string.IsNullOrWhiteSpace(tenantDatabaseName))
        {
            return null;
        }

        var templateConnectionString = _configuration.GetConnectionString(DefaultTenantTemplateConnectionStringName) ??
                                       _configuration.GetConnectionString(ConfigurationConsts.IdentityDbConnectionStringKey);
        if (string.IsNullOrWhiteSpace(templateConnectionString))
        {
            return null;
        }

        var derivedConnectionString = ReplaceDatabaseName(templateConnectionString, tenantDatabaseName);
        _logger.LogWarning(
            "Resolved tenant DB secret '{SecretReference}' for tenant '{TenantKey}' by deriving local development database '{DatabaseName}'.",
            secretReference,
            tenantKey,
            tenantDatabaseName);

        return derivedConnectionString;
    }

    private static string? TryResolveDevelopmentDatabaseName(string tenantKey, string serviceKey, string secretReference)
    {
        if (string.IsNullOrWhiteSpace(tenantKey))
        {
            return null;
        }

        var normalizedServiceKey = serviceKey.Trim();
        if (string.Equals(normalizedServiceKey, "BlazorApiUser", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.users";
        }

        if (string.Equals(normalizedServiceKey, "BlazorWebApiFiles", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.files";
        }

        if (string.Equals(normalizedServiceKey, "NotificationService", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.notification";
        }

        if (string.Equals(normalizedServiceKey, "Webhooks.API", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.webhook";
        }

        if (secretReference.EndsWith("/user-api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.users";
        }

        if (secretReference.EndsWith("/file-api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.files";
        }

        if (secretReference.EndsWith("/notification-api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.notification";
        }

        if (secretReference.EndsWith("/webhook-api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{tenantKey}.webhook";
        }

        return null;
    }

    private static string ReplaceDatabaseName(string templateConnectionString, string databaseName)
    {
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = templateConnectionString
        };

        if (builder.ContainsKey("Database"))
        {
            builder["Database"] = databaseName;
            return builder.ConnectionString;
        }

        if (builder.ContainsKey("Initial Catalog"))
        {
            builder["Initial Catalog"] = databaseName;
            return builder.ConnectionString;
        }

        builder["Database"] = databaseName;
        return builder.ConnectionString;
    }

    private static bool LooksLikeConnectionString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Contains('=') &&
               value.Contains(';') &&
               (value.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Host=", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("Data Source=", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDevelopment()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCentralRequest()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return true;
        }

        if (!TryGetCentralUri(out var centralUri))
        {
            return true;
        }

        var requestHost = httpContext.Request.Host.Host;
        if (string.IsNullOrWhiteSpace(requestHost))
        {
            return true;
        }

        var requestPort = httpContext.Request.Host.Port ?? GetDefaultPort(httpContext.Request.Scheme);
        var centralPort = centralUri.IsDefaultPort ? GetDefaultPort(centralUri.Scheme) : centralUri.Port;

        if (requestPort != centralPort)
        {
            return false;
        }

        return string.Equals(requestHost, centralUri.Host, StringComparison.OrdinalIgnoreCase) ||
               IsLocalLoopbackHost(requestHost);
    }

    private bool TryGetCentralUri(out Uri centralUri)
    {
        var centralBaseUrl = _options.Value.CentralBaseUrl?.Trim();
        if (!Uri.TryCreate(centralBaseUrl, UriKind.Absolute, out centralUri!))
        {
            _logger.LogWarning(
                "TenantIdentityDbResolution:CentralBaseUrl is missing or invalid. Falling back to the central identity database.");
            return false;
        }

        return true;
    }

    private static string NormalizeServiceKey(string? rawServiceKey)
    {
        return string.IsNullOrWhiteSpace(rawServiceKey) ? "BlazorApiUser" : rawServiceKey.Trim();
    }

    private static int GetDefaultPort(string? scheme)
    {
        return string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;
    }

    private static bool IsLocalLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
