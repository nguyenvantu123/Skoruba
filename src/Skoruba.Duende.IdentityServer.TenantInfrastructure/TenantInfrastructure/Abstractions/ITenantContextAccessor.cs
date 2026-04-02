namespace TenantInfrastructure.Abstractions;

public interface ITenantContextAccessor
{
    TenantContext? Current { get; }
    void Set(TenantContext context);
    void Clear();
}
