using Xunit;

namespace Skoruba.Duende.IdentityServer.TenantInfrastructure.UnitTests.Common;

/// <summary>
/// xUnit collection marker used by tests that mutate process-wide environment variables.
/// Test classes attributed with <c>[Collection(EnvironmentVariableCollection.Name)]</c>
/// are guaranteed to run sequentially with each other, eliminating env-var races between
/// test classes. (Tests within a single xUnit test class already run sequentially by
/// default.)
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "EnvironmentVariables";
}
