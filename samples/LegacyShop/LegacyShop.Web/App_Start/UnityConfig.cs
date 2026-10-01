using System.Web.Mvc;
using LegacyShop.Core.Services;
using Microsoft.Practices.Unity;
using Unity.Mvc5;

namespace LegacyShop.Web
{
    public static class UnityConfig
    {
        public static void RegisterComponents()
        {
            var container = new UnityContainer();

            container.RegisterType<IProdutoService, ProdutoService>(new HierarchicalLifetimeManager());
            container.RegisterType<IEmailService, EmailService>();
            container.RegisterType<ICacheService, CacheService>(new ContainerControlledLifetimeManager());

            DependencyResolver.SetResolver(new UnityDependencyResolver(container));
        }
    }
}
