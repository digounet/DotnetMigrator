using System.Data.Entity;
using LegacyShop.Core.Domain;

namespace LegacyShop.Core.Data
{
    public class ShopContext : DbContext
    {
        public ShopContext() : base("name=DefaultConnection")
        {
            Database.SetInitializer<ShopContext>(null);
        }

        public DbSet<ProdutoEntidade> Produtos { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProdutoEntidade>().ToTable("Produto");
            modelBuilder.Entity<ProdutoEntidade>().Property(p => p.Preco).HasPrecision(18, 2);
        }
    }
}
