using AutoMapper;
using LegacyShop.Core.Domain;
using LegacyShop.Web.Models;

namespace LegacyShop.Web
{
    public static class AutoMapperConfig
    {
        public static void Initialize()
        {
            Mapper.Initialize(cfg =>
            {
                cfg.CreateMap<ProdutoEntidade, Produto>();
                cfg.CreateMap<Produto, ProdutoEntidade>();
            });
        }
    }
}
