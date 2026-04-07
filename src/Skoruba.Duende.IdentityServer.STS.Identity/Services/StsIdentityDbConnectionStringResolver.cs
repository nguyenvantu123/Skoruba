using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
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

    public StsIdentityDbConnectionStringResolver(
        IConfiguration configuration,
        ITenantContextAccessor tenantContextAccessor,
        IHttpContextAccessor httpContextAccessor,
        IOptions<TenantIdentityDbResolutionConfiguration> options)
    {
        _configuration = configuration;
        _tenantContextAccessor = tenantContextAccessor;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
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

        return centralConnectionString;
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
                return configuredValue.Trim();
            }
        }

        var configuredConnectionString = _configuration.GetConnectionString(secretReference);
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
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

    private static string NormalizeServiceKey(string? rawServiceKey)
    {
        return string.IsNullOrWhiteSpace(rawServiceKey) ? "BlazorApiUser" : rawServiceKey.Trim();
    }
}
