namespace TenantInfrastructure.Wiring;

/// <summary>
/// Parses a configured database provider string (e.g. from
/// <see cref="TenantInfrastructureOptions.DatabaseProvider"/> or the
/// <c>DatabaseProviderConfiguration__ProviderType</c> environment variable) into the
/// internal <see cref="TenantDatabaseProvider"/> enum.
/// <para>
/// Centralised here so that <c>ServiceCollectionExtensions.AddTenantInfrastructure</c>
/// and <c>MasterDbContextFactory</c> share a single, consistent parsing rule.
/// </para>
/// </summary>
internal static class TenantDatabaseProviderParser
{
    /// <summary>
    /// Comma-separated list of supported provider names, used in error messages so the
    /// operator gets a clear, copyable hint. Kept in sync with the <see cref="TenantDatabaseProvider"/>
    /// enum members.
    /// </summary>
    private const string SupportedProvidersList = "SqlServer, PostgreSQL, MySql";

    /// <summary>
    /// Parses <paramref name="value"/> case-insensitively into a <see cref="TenantDatabaseProvider"/>.
    /// Throws an <see cref="InvalidOperationException"/> when <paramref name="value"/> is
    /// <c>null</c>, empty, whitespace, or does not match any supported provider name.
    /// </summary>
    /// <param name="value">Configured provider name. Expected one of <c>SqlServer</c>,
    /// <c>PostgreSQL</c>, <c>MySql</c> (case-insensitive).</param>
    /// <returns>The matching <see cref="TenantDatabaseProvider"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the value is missing or unsupported.</exception>
    public static TenantDatabaseProvider Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "DatabaseProvider value is required for TenantInfrastructure. " +
                $"Supported values: {SupportedProvidersList}.");
        }

        if (string.Equals(value, nameof(TenantDatabaseProvider.SqlServer), StringComparison.OrdinalIgnoreCase))
        {
            return TenantDatabaseProvider.SqlServer;
        }

        if (string.Equals(value, nameof(TenantDatabaseProvider.PostgreSQL), StringComparison.OrdinalIgnoreCase))
        {
            return TenantDatabaseProvider.PostgreSQL;
        }

        if (string.Equals(value, nameof(TenantDatabaseProvider.MySql), StringComparison.OrdinalIgnoreCase))
        {
            return TenantDatabaseProvider.MySql;
        }

        throw new InvalidOperationException(
            $"DatabaseProvider value '{value}' is not supported for TenantInfrastructure. " +
            $"Supported values: {SupportedProvidersList}.");
    }
}
