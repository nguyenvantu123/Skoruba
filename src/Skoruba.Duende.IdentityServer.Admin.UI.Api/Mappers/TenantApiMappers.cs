using AutoMapper;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Mappers
{
    public static class TenantApiMappers
    {
        static TenantApiMappers()
        {
            Mapper = new MapperConfiguration(cfg => cfg.AddProfile<TenantApiMapperProfile>())
                .CreateMapper();
        }

        internal static IMapper Mapper { get; }

        public static T ToTenantApiModel<T>(this object source)
        {
            return Mapper.Map<T>(source);
        }
    }
}
