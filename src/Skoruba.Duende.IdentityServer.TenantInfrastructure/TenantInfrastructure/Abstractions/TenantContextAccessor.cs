namespace TenantInfrastructure.Abstractions;

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantContext?> _current = new();

    public TenantContext? Current => _current.Value;

    public void Set(TenantContext context) => _current.Value = context;

    public void Clear() => _current.Value = null;
}
