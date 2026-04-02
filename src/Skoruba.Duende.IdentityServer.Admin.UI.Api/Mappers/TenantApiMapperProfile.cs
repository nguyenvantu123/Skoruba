using AutoMapper;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using Skoruba.Duende.IdentityServer.Admin.UI.Api.Dtos.Tenants;

namespace Skoruba.Duende.IdentityServer.Admin.UI.Api.Mappers
{
    public class TenantApiMapperProfile : Profile
    {
        public TenantApiMapperProfile()
        {
            CreateMap<TenantDto, TenantApiDto>(MemberList.Destination);
            CreateMap<PublicTenantDto, PublicTenantApiDto>(MemberList.Destination);
            CreateMap<TenantRegistryLookupResultDto, TenantRegistryLookupResultApiDto>(MemberList.Destination);
            CreateMap<TenantCreateApiDto, TenantCreateDto>(MemberList.Destination);
            CreateMap<TenantCloneApiDto, TenantCloneDto>(MemberList.Destination);
            CreateMap<TenantUpdateApiDto, TenantUpdateDto>(MemberList.Destination);
            CreateMap<TenantAdminDto, TenantAdminApiDto>(MemberList.Destination);
        }
    }
}
