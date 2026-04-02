using AutoMapper;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Mappers
{
    public static class TenantMappers
    {
        static TenantMappers()
        {
            Mapper = new MapperConfiguration(cfg => cfg.AddProfile<TenantMapperProfile>())
                .CreateMapper();
        }

        internal static IMapper Mapper { get; }

        public static TenantDto ToModel(this TenantInfo tenant)
        {
            return Mapper.Map<TenantDto>(tenant);
        }

        public static List<TenantDto> ToModel(this IEnumerable<TenantInfo> tenants)
        {
            return Mapper.Map<List<TenantDto>>(tenants);
        }

        public static List<PublicTenantDto> ToPublicModel(this IEnumerable<TenantInfo> tenants)
        {
            return Mapper.Map<List<PublicTenantDto>>(tenants);
        }

        public static TenantInfo ToEntity(this TenantCreateDto tenant)
        {
            return Mapper.Map<TenantInfo>(tenant);
        }

        public static void MapToEntity(this TenantUpdateDto model, TenantInfo tenant)
        {
            Mapper.Map(model, tenant);
        }
    }
}
