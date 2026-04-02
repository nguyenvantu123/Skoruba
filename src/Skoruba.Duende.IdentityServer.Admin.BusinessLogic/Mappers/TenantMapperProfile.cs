using AutoMapper;
using Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Dtos.Tenant;
using TenantInfrastructure.MasterDb;

namespace Skoruba.Duende.IdentityServer.Admin.BusinessLogic.Mappers
{
    public class TenantMapperProfile : Profile
    {
        public TenantMapperProfile()
        {
            CreateMap<TenantInfo, TenantDto>(MemberList.Destination);
            CreateMap<TenantInfo, PublicTenantDto>(MemberList.Destination);
            CreateMap<TenantCreateDto, TenantInfo>(MemberList.Source);
            CreateMap<TenantUpdateDto, TenantInfo>(MemberList.Source);
        }
    }
}
