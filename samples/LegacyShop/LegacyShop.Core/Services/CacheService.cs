using System;
using System.IO;
using System.Runtime.Caching;
using System.Runtime.Serialization.Formatters.Binary;

namespace LegacyShop.Core.Services
{
    public interface ICacheService
    {
        T Obter<T>(string chave, Func<T> fabrica, int minutos = 10) where T : class;
        T Clonar<T>(T objeto);
    }

    public class CacheService : ICacheService
    {
        private readonly ObjectCache _cache = MemoryCache.Default;

        public T Obter<T>(string chave, Func<T> fabrica, int minutos = 10) where T : class
        {
            if (_cache.Get(chave) is T existente)
            {
                return existente;
            }
            var valor = fabrica();
            _cache.Set(chave, valor, new CacheItemPolicy { AbsoluteExpiration = DateTimeOffset.Now.AddMinutes(minutos) });
            return valor;
        }

        public T Clonar<T>(T objeto)
        {
            var formatter = new BinaryFormatter();
            using (var stream = new MemoryStream())
            {
                formatter.Serialize(stream, objeto);
                stream.Position = 0;
                return (T)formatter.Deserialize(stream);
            }
        }
    }
}
