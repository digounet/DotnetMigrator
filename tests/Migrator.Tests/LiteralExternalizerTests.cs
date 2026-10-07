using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>Fixed URLs, e-mails and credentials in C# literals become configuration reads; unsafe spots are reported, not rewritten.</summary>
public sealed class LiteralExternalizerTests
{
    private static (string Text, LiteralExternalizer.Session Session) Run(string code, string file = "AppConfig.cs")
    {
        var session = new LiteralExternalizer.Session();
        return (LiteralExternalizer.Externalize(code, file, session), session);
    }

    [Fact]
    public void Urls_emails_and_credentials_become_appsettings_reads_and_const_becomes_static_readonly()
    {
        var (text, session) = Run("""
            public static class AppConfig
            {
                public const string ErpUrl = "https://erp.exemplo.com.br/api/protocolo";
                public static readonly string EmailSuporte = "suporte@exemplo.com.br";
                public const string TokenIntegracaoErp = "erp-9f3b2c1d-token-legado";
                public static readonly string ConexaoAntiga = "Server=srv;Database=X;User Id=u;Password=Senha@123;";
                private readonly string _chave = "AKIAIOSFODNN7EXAMPLE";
                public static string Nome => "Loja";
            }
            """);
        Assert.Contains("public static readonly string ErpUrl = System.Configuration.ConfigurationManager.AppSettings[\"Urls:ErpUrl\"];", text);
        Assert.Contains("EmailSuporte = System.Configuration.ConfigurationManager.AppSettings[\"Emails:EmailSuporte\"];", text);
        Assert.Contains("public static readonly string TokenIntegracaoErp = System.Configuration.ConfigurationManager.AppSettings[\"Credenciais:TokenIntegracaoErp\"];", text);
        Assert.Contains("ConexaoAntiga = System.Configuration.ConfigurationManager.AppSettings[\"Credenciais:ConexaoAntiga\"];", text);
        Assert.Contains("_chave = System.Configuration.ConfigurationManager.AppSettings[\"Credenciais:Chave\"];", text);
        Assert.Contains("public static string Nome => \"Loja\";", text);
        Assert.DoesNotContain("erp-9f3b2c1d", text);
        Assert.DoesNotContain("Senha@123", text);
        Assert.Equal(5, session.Literals.Count(l => l.Rewritten));
        var token = session.Literals.Single(l => l.Key == "Credenciais:TokenIntegracaoErp");
        Assert.Equal((SettingKind.Secret, "erp-9f3b2c1d-token-legado", 5), (token.Kind, token.Value, token.Line));
    }

    [Fact]
    public void Same_value_in_two_files_shares_one_key_and_keys_do_not_collide()
    {
        var session = new LiteralExternalizer.Session();
        LiteralExternalizer.Externalize("class A { string u = \"https://erp.exemplo.com.br/\"; }", "A.cs", session);
        var b = LiteralExternalizer.Externalize("class B { string u = \"https://erp.exemplo.com.br/\"; string v = \"https://outro.exemplo.com.br/\"; }", "B.cs", session);
        Assert.Contains("AppSettings[\"Urls:U\"]", b);
        Assert.Contains("AppSettings[\"Urls:V\"]", b);
        Assert.Equal(["Urls:U", "Urls:V"], session.Keys.Order());
    }

    [Fact]
    public void Unsafe_contexts_are_reported_but_not_rewritten()
    {
        var (text, session) = Run("""
            [WebService(Namespace = "http://exemplo.com.br/servicos")]
            public class Svc
            {
                [Route("https://api.exemplo.com.br/x")] public void A() { }
                public void B(string url = "https://padrao.exemplo.com.br") { }
                public string C(string tipo) { switch (tipo) { case "https://caso.exemplo.com.br": return ""; default: return ""; } }
                public string D(int id) => $"https://api.exemplo.com.br/itens/{id}";
                public const string Xml = @"https://verbatim.exemplo.com.br";
                public string Ok = "https://ok.exemplo.com.br";
            }
            """);
        Assert.Contains("[Route(\"https://api.exemplo.com.br/x\")]", text);
        Assert.Contains("string url = \"https://padrao.exemplo.com.br\"", text);
        Assert.Contains("case \"https://caso.exemplo.com.br\"", text);
        Assert.Contains("$\"https://api.exemplo.com.br/itens/{id}\"", text);
        Assert.Contains("@\"https://verbatim.exemplo.com.br\"", text);
        Assert.Contains("Ok = System.Configuration.ConfigurationManager.AppSettings[\"Urls:Ok\"]", text);
        Assert.Single(session.Literals, l => l.Rewritten);
        Assert.Contains(session.Literals, l => l.SkipReason == "literal em atributo");
        Assert.Contains(session.Literals, l => l.SkipReason == "valor padrão de parâmetro");
        Assert.Contains(session.Literals, l => l.SkipReason == "rótulo de switch/case");
        Assert.Contains(session.Literals, l => l.SkipReason == "string verbatim/raw");
        Assert.DoesNotContain(session.Literals, l => l.Value.Contains("servicos")); // XML namespaces are not endpoints
    }

    [Fact]
    public void Schema_urls_localhost_and_non_credential_identifiers_are_ignored()
    {
        var (text, session) = Run("""
            class X {
                const string Ns = "http://schemas.microsoft.com/winfx/2006/xaml";
                string local = "http://localhost:5000/api";
                string tokenType = "Bearer";
                string passwordLabel = "Digite a senha";
                string cancel = "CancellationToken";
                string s = "SELECT * FROM T";
            }
            """);
        Assert.Empty(session.Literals);
        Assert.Contains("const string Ns", text);
    }
}
