using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>Tables, columns and procedures found in code: SQL literals, Dapper, ADO.NET readers, EF6/EF Core mappings, EDMX, .sql files.</summary>
public sealed class DataAccessAnalyzerTests
{
    private static ProjectInfo Project(string name = "Loja.Core", bool vb = false) => new() { ProjectPath = $@"C:\src\{name}\{name}.{(vb ? "vbproj" : "csproj")}", Name = name, Kind = ProjectKind.ClassLibrary, Language = vb ? "VB" : "C#" };

    private static DataAccessScan Scan(string code, string path = "Repo.cs", bool vb = false) => DataAccessAnalyzer.Scan(Project(vb: vb), [(path, code)]);

    private static TableAccess Table(DataAccessScan scan, string name) => Assert.Single(scan.Tables, t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Ado_net_insert_reveals_table_columns_and_operation()
    {
        var scan = Scan("""
            using System.Data.SqlClient;
            class Repo {
                void Gravar(string numero, decimal valor) {
                    using (var conexao = new SqlConnection(_cs))
                    using (var comando = new SqlCommand("INSERT INTO PedidoImportado (Numero, Cliente, Valor, Data) VALUES (@n, @c, @v, @d)", conexao))
                    {
                        comando.Parameters.AddWithValue("@n", numero);
                        comando.ExecuteNonQuery();
                    }
                }
            }
            """);
        var t = Table(scan, "PedidoImportado");
        Assert.Equal(["Cliente", "Data", "Numero", "Valor"], t.Columns);
        Assert.Equal(["INSERT"], t.Operations);
        Assert.Contains("ADO.NET", t.Access);
        Assert.Equal("Repo.cs:5", t.Locations[0]);
        Assert.Equal("SQL Server", scan.TechnologyHint);
    }

    [Fact]
    public void Dapper_and_connection_string_name_are_captured()
    {
        var scan = Scan("""
            using Dapper;
            class Repo {
                private readonly string _cs = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;
                void Remover(int id) { using (var c = new SqlConnection(_cs)) c.Execute("DELETE FROM Produto WHERE Id = @id", new { id }); }
                void Atualizar(Produto p) { using (var c = new SqlConnection(_cs)) c.Execute("UPDATE Produto SET Nome = @Nome, Preco = @Preco WHERE Id = @Id", p); }
            }
            """);
        var t = Table(scan, "Produto");
        Assert.Equal(["DELETE", "UPDATE"], t.Operations);
        Assert.Equal(["Id", "Nome", "Preco"], t.Columns);
        Assert.Contains("Dapper", t.Access);
        Assert.Equal(["DefaultConnection"], t.ConnectionNames);
    }

    [Fact]
    public void Select_with_joins_aliases_and_three_part_name()
    {
        var scan = Scan("""
            class Repo {
                const string Sql = @"SELECT p.Id, p.Numero, c.Nome AS Cliente, SUM(i.Valor) Total
                                     FROM Vendas.dbo.Pedido p
                                     INNER JOIN Cliente c ON c.Id = p.ClienteId
                                     LEFT JOIN ItemPedido i WITH (NOLOCK) ON i.PedidoId = p.Id
                                     WHERE p.Data >= @inicio AND c.Ativo = 1
                                     GROUP BY p.Id, p.Numero, c.Nome
                                     ORDER BY p.Data DESC";
            }
            """);
        var pedido = Table(scan, "Pedido");
        Assert.Equal("Vendas", pedido.Database);
        Assert.True(pedido.DatabaseFromSql);
        Assert.Equal("dbo", pedido.Schema);
        Assert.Equal(["ClienteId", "Data", "Id", "Numero"], pedido.Columns);
        Assert.Equal(["Ativo", "Id", "Nome"], Table(scan, "Cliente").Columns);
        Assert.Equal(["PedidoId", "Valor"], Table(scan, "ItemPedido").Columns);
        Assert.All(scan.Tables, t => Assert.Equal(["SELECT"], t.Operations));
        Assert.DoesNotContain(scan.Tables, t => t.Name is "p" or "c" or "i" or "NOLOCK" or "Total");
    }

    [Fact]
    public void Concatenated_pieces_form_one_statement()
    {
        var scan = Scan("""
            class Repo {
                string Sql() => "SELECT Codigo, Descricao " +
                                "FROM Material " +
                                "WHERE Grupo = @g";
            }
            """);
        var t = Table(scan, "Material");
        Assert.Equal(["Codigo", "Descricao", "Grupo"], t.Columns);
    }

    [Fact]
    public void Visual_basic_literals_and_reader_columns()
    {
        var scan = Scan("""
            Imports System.Data.SqlClient
            Public Class Gerador
                Private ReadOnly _conexao As String = ConfigurationManager.ConnectionStrings("Relatorios").ConnectionString
                Public Sub Gerar(mes As Integer)
                    Dim comando As New SqlCommand("SELECT * FROM Vendas WHERE Mes = " & mes & " AND Ano = " & ano, conexao)
                    Dim leitor = comando.ExecuteReader()
                    While leitor.Read()
                        planilha.Cells(linha, 1) = leitor("Produto")
                        planilha.Cells(linha, 2) = leitor("Valor")
                    End While
                End Sub
            End Class
            """, "Gerador.vb", vb: true);
        var t = Table(scan, "Vendas");
        Assert.Equal(["*", "Mes", "Produto", "Valor"], t.Columns);
        Assert.Equal(["Relatorios"], t.ConnectionNames);
        Assert.Equal(["SELECT"], t.Operations);
    }

    [Fact]
    public void Stored_procedures_with_parameters_ado_net_and_dapper()
    {
        var scan = Scan("""
            class Repo {
                void Gravar() {
                    var cmd = new SqlCommand("dbo.sp_GravarPedido", conn) { CommandType = CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("@Numero", numero);
                    cmd.Parameters.Add("@Valor", SqlDbType.Decimal).Value = valor;
                    cmd.ExecuteNonQuery();
                }
                void Listar() { conn.Query<Pedido>("sp_ListarPedidos", new { ano }, commandType: CommandType.StoredProcedure); }
                void Executar() { conn.Execute("EXEC sp_Fechamento @Mes = @m, @Ano = @a", new { m, a }); }
            }
            """);
        var gravar = Table(scan, "sp_GravarPedido");
        Assert.Equal(DataObjectKind.StoredProcedure, gravar.Kind);
        Assert.Equal("dbo", gravar.Schema);
        Assert.Equal(["@Numero", "@Valor"], gravar.Columns);
        Assert.Equal(DataObjectKind.StoredProcedure, Table(scan, "sp_ListarPedidos").Kind);
        var fechamento = Table(scan, "sp_Fechamento");
        Assert.Equal(DataObjectKind.StoredProcedure, fechamento.Kind);
        Assert.Equal(["@Ano", "@Mes"], fechamento.Columns);
        Assert.Equal(["EXEC"], fechamento.Operations);
    }

    [Fact]
    public void Entity_framework_dbsets_resolve_to_tables_with_entity_columns()
    {
        var context = """
            using System.Data.Entity;
            public class ShopContext : DbContext {
                public ShopContext() : base("name=DefaultConnection") { }
                public DbSet<ProdutoEntidade> Produtos { get; set; }
                public DbSet<Categoria> Categorias { get; set; }
                protected override void OnModelCreating(DbModelBuilder modelBuilder) {
                    modelBuilder.Entity<ProdutoEntidade>().ToTable("Produto");
                    modelBuilder.Entity<ProdutoEntidade>().Property(p => p.Preco).HasColumnName("PrecoUnitario");
                    modelBuilder.Entity<ProdutoEntidade>().Ignore(p => p.Calculado);
                }
            }
            """;
        var entities = """
            public class ProdutoEntidade {
                public int Id { get; set; }
                public string Nome { get; set; }
                public decimal Preco { get; set; }
                public decimal Calculado { get; set; }
                [NotMapped] public string Temporario { get; set; }
                public virtual Categoria Categoria { get; set; }
                public virtual ICollection<ItemPedido> Itens { get; set; }
            }
            [Table("Categorias", Schema = "cadastro")]
            public class Categoria { public int Id { get; set; } [Column("Descricao")] public string Nome { get; set; } }
            public class ItemPedido { public int Id { get; set; } }
            """;
        var project = Project();
        var scan = DataAccessAnalyzer.Scan(project, [("Data/ShopContext.cs", context), ("Domain/Entidades.cs", entities)]);
        var migrated = new Migrator.Core.Migration.MigratedProject(new ProjectResult { Project = project }, null, new Migrator.Core.Migration.OutputPlan(),
            ApplicationProfiler.Analyze(project, [], System.Xml.Linq.XElement.Parse("""<configuration><connectionStrings><add name="DefaultConnection" connectionString="Data Source=srv;Initial Catalog=Loja;User Id=u;Password=p" providerName="System.Data.SqlClient" /></connectionStrings></configuration>""")));
        migrated.Result.DataScan = scan;

        DataAccessAnalyzer.Resolve([migrated]);

        var produto = Assert.Single(migrated.Result.DataAccess, t => t.Name == "Produto");
        Assert.Equal(["Id", "Nome", "PrecoUnitario"], produto.Columns);          // HasColumnName applied; Ignore/NotMapped/navigations excluded
        Assert.Contains("EF6", produto.Access);
        Assert.Equal(["EF (leitura)"], produto.Operations);                      // no SaveChanges anywhere
        Assert.Equal("Loja", produto.Database);
        Assert.Equal("SQL Server", produto.Technology);
        Assert.True(produto.DatabaseResolved);
        var categoria = Assert.Single(migrated.Result.DataAccess, t => t.Name == "Categorias");
        Assert.Equal("cadastro", categoria.Schema);
        Assert.Equal(["Descricao", "Id"], categoria.Columns);
    }

    [Fact]
    public void Ef6_convention_pluralizes_and_ef_core_uses_the_dbset_name()
    {
        Assert.Equal("Produtos", DataAccessAnalyzer.Pluralize("Produto"));
        Assert.Equal("Categories", DataAccessAnalyzer.Pluralize("Category"));
        Assert.Equal("Statuses", DataAccessAnalyzer.Pluralize("Status"));
        var scan = Scan("""
            using Microsoft.EntityFrameworkCore;
            public class AppDb : DbContext { public DbSet<Cliente> Clientes { get; set; } }
            public class Cliente { public int Id { get; set; } }
            """);
        Assert.Equal("Clientes", Assert.Single(scan.EntityRefs).PropertyName);
        Assert.True(scan.EntityRefs[0].EfCore);
    }

    [Fact]
    public void Sql_files_and_edmx_models_are_read()
    {
        var sql = "SELECT TOP 8 Id, Nome, Descricao, Preco FROM Produto WHERE Destaque = 1 ORDER BY Nome";
        var edmx = """
            <edmx:Edmx Version="3.0" xmlns:edmx="http://schemas.microsoft.com/ado/2009/11/edmx">
              <edmx:Runtime>
                <edmx:StorageModels>
                  <Schema Namespace="LojaModel.Store" xmlns="http://schemas.microsoft.com/ado/2009/11/edm/ssdl">
                    <EntityType Name="Cliente"><Key><PropertyRef Name="Id" /></Key><Property Name="Id" Type="int" Nullable="false" /><Property Name="Nome" Type="nvarchar" MaxLength="100" /></EntityType>
                    <Function Name="sp_Fechar" Schema="dbo"><Parameter Name="Ano" Type="int" Mode="In" /></Function>
                    <EntityContainer Name="LojaModelStoreContainer"><EntitySet Name="Cliente" EntityType="Self.Cliente" Schema="dbo" store:Type="Tables" xmlns:store="http://schemas.microsoft.com/ado/2007/12/edm/EntityStoreSchemaGenerator" /></EntityContainer>
                  </Schema>
                </edmx:StorageModels>
              </edmx:Runtime>
            </edmx:Edmx>
            """;
        var scan = DataAccessAnalyzer.Scan(Project(), [("Sql/Consulta.sql", sql), ("Model/Loja.edmx", edmx)]);
        var produto = Table(scan, "Produto");
        Assert.Equal(["Descricao", "Destaque", "Id", "Nome", "Preco"], produto.Columns);
        Assert.Contains("arquivo .sql", produto.Access);
        var cliente = Table(scan, "Cliente");
        Assert.Equal(["Id", "Nome"], cliente.Columns);
        Assert.Contains("EDMX (EF6 Database First)", cliente.Access);
        Assert.Equal(["@Ano"], Table(scan, "sp_Fechar").Columns);
    }

    [Fact]
    public void Linq_query_syntax_and_plain_text_are_not_sql()
    {
        var scan = Scan("""
            class X {
                void M() {
                    var q = from p in produtos select p;
                    var msg = "Update your profile from the settings page";
                    var other = "Select the file to delete from the list";
                    Log("DELETE requested");
                }
            }
            """);
        Assert.Empty(scan.Tables);
    }
}
