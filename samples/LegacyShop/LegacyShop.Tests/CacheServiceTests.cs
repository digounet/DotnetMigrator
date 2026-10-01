using System;
using LegacyShop.Core.Domain;
using LegacyShop.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegacyShop.Tests
{
    [TestClass]
    public class CacheServiceTests
    {
        [TestMethod]
        public void Obter_ReutilizaValorEmCache()
        {
            var cache = new CacheService();
            var chamadas = 0;
            Func<ProdutoEntidade> fabrica = () => { chamadas++; return new ProdutoEntidade { Id = 1 }; };

            cache.Obter("p1", fabrica);
            cache.Obter("p1", fabrica);

            Assert.AreEqual(1, chamadas);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void Obter_ChaveNula_LancaExcecao()
        {
            new CacheService().Obter<ProdutoEntidade>(null, () => null);
        }
    }
}
