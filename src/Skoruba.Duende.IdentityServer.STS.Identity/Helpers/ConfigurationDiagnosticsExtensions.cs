using System.Collections.Generic;
using System.Linq;
using Duende.IdentityServer.Configuration;
using Microsoft.Extensions.Configuration;
using Serilog;
using Skoruba.Duende.IdentityServer.STS.Identity.Configuration;

namespace Skoruba.Duende.IdentityServer.STS.Identity.Helpers
{
    public static class ConfigurationDiagnosticsExtensions
    {
        private static readonly string[] DiagnosticKeys =
        {
            nameof(ExternalProvidersConfiguration),
            $"{nameof(ExternalProvidersConfiguration)}:UseGitHubProvider",
            $"{nameof(ExternalProvidersConfiguration)}:UseAzureAdProvider",
            nameof(IdentityServerOptions),
            $"{nameof(IdentityServerOptions)}:KeyManagement:Enabled"
        };

        public static void ValidateStartupConfiguration(this IConfiguration configuration)
        {
            if (configuration is not IConfigurationRoot root)
            {
                Log.Warning("Configuration diagnostics skipped because IConfigurationRoot is unavailable.");
                return;
            }

            ValidateObjectSection(configuration, nameof(ExternalProvidersConfiguration));
            ValidateObjectSection(configuration, nameof(IdentityServerOptions));

            foreach (var key in DiagnosticKeys)
            {
                LogProviderChain(root, key);
            }
        }

        private static void ValidateObjectSection(IConfiguration configuration, string sectionKey)
        {
            var section = configuration.GetSection(sectionKey);
            var scalarValue = configuration[sectionKey];
            var hasChildren = section.GetChildren().Any();

            if (!string.IsNullOrWhiteSpace(scalarValue))
            {
                Log.Error(
                    "Configuration key {ConfigKey} has scalar value '{ConfigValue}' and overrides nested section values. Remove this root key.",
                    sectionKey,
                    scalarValue);
            }

            if (!hasChildren)
            {
                Log.Warning("Configuration section {ConfigKey} has no child values.", sectionKey);
            }
        }

        private static void LogProviderChain(IConfigurationRoot root, string key)
        {
            var providerValues = new List<string>();

            foreach (var provider in root.Providers)
            {
                if (provider.TryGet(key, out var value))
                {
                    providerValues.Add($"{provider.GetType().Name}='{value}'");
                }
            }

            if (providerValues.Count == 0)
            {
                Log.Warning("Configuration key {ConfigKey} is not set by any provider.", key);
                return;
            }

            var winner = providerValues[^1];
            Log.Information(
                "Configuration key {ConfigKey} resolved value from {WinningProvider}. Provider chain: {ProviderChain}",
                key,
                winner,
                string.Join(" -> ", providerValues));
        }
    }
}
