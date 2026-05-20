#nullable enable
using System;

namespace Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Common;

/// <summary>
/// Test helper that sets a process-wide environment variable for the lifetime of the
/// scope and restores the previous value (including the unset state) on <see cref="Dispose"/>.
/// <para>
/// Tests that mutate environment variables must wrap their setup in this scope so that
/// changes never leak to subsequent tests in the same process. Combined with an xUnit
/// <see cref="Xunit.CollectionAttribute"/> on the test class, this also keeps env-var
/// reads/writes serialised across test classes.
/// </para>
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;
    private bool _disposed;

    /// <summary>
    /// Captures the current value of <paramref name="name"/>, then sets it to
    /// <paramref name="value"/>. Passing <c>null</c> for <paramref name="value"/>
    /// removes the variable for the duration of the scope.
    /// </summary>
    public EnvironmentVariableScope(string name, string? value)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Environment variable name must be provided.", nameof(name));
        }

        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Environment.SetEnvironmentVariable(_name, _previousValue);
        _disposed = true;
    }
}
