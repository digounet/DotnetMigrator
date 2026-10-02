using System.Text.Json.Nodes;
using Migrator.Core.Migration;

namespace Migrator.Tests;

public class SecretsTests
{
    private static ConfigMigrationResult Config(string appsettings, (string Env, string Json)? environment = null)
    {
        var config = new ConfigMigrationResult { AppSettingsJson = appsettings };
        if (environment is { } e) config.EnvironmentJson[e.Env] = e.Json;
        return config;
    }

    [Fact]
    public void Moves_passwords_keys_and_connection_strings_out_of_appsettings()
    {
        var config = Config("""
            {
              "ConnectionStrings": {
                "DefaultConnection": "Data Source=(LocalDb)\\MSSQLLocalDB;Initial Catalog=Loja;Integrated Security=True",
                "Relatorios": "Data Source=srv;Initial Catalog=Rel;User ID=app;Password=Senha@123"
              },
              "Smtp": { "Host": "smtp.exemplo.com.br", "UserName": "loja", "Password": "SmtpSenha!" },
              "AppSettings": { "ItensPorPagina": "20", "PagamentoApiKey": "sk_test_123456789", "Smtp.From": "loja@exemplo.com.br" }
            }
            """, ("Production", """{ "ConnectionStrings": { "Relatorios": "Data Source=prod;Initial Catalog=Rel;User ID=app;Password=Prod!" } }"""));

        var plan = SecretsExtractor.Extract(config, "LegacyShop", "LegacyShop.Web");

        Assert.Equal(["ConnectionStrings:Relatorios", "Smtp:Password", "AppSettings:PagamentoApiKey", "ConnectionStrings:Relatorios"], plan.Secrets.Select(s => s.ConfigPath));
        var relatorios = plan.Secrets[0];
        Assert.Equal("legacyshop/legacyshop.web/ConnectionStrings/Relatorios", relatorios.SecretName);
        Assert.Equal("ConnectionStrings__Relatorios", relatorios.EnvironmentVariable);
        Assert.Null(relatorios.Environment);
        Assert.Equal("Production", plan.Secrets[3].Environment);
        Assert.Equal("legacyshop/legacyshop.web/production/ConnectionStrings/Relatorios", plan.Secrets[3].SecretName);

        var scrubbed = JsonNode.Parse(config.AppSettingsJson!)!;
        Assert.Equal("<secret: legacyshop/legacyshop.web/ConnectionStrings/Relatorios>", (string)scrubbed["ConnectionStrings"]!["Relatorios"]!);
        Assert.Contains("Integrated Security=True", (string)scrubbed["ConnectionStrings"]!["DefaultConnection"]!); // no password: untouched
        Assert.Equal("<secret: legacyshop/legacyshop.web/Smtp/Password>", (string)scrubbed["Smtp"]!["Password"]!);
        Assert.Equal("loja", (string)scrubbed["Smtp"]!["UserName"]!);
        Assert.Equal("20", (string)scrubbed["AppSettings"]!["ItensPorPagina"]!);
        Assert.DoesNotContain("Senha@123", config.AppSettingsJson);
        Assert.DoesNotContain("Prod!", config.EnvironmentJson["Production"]);
    }

    [Fact]
    public void Produces_appsettings_shaped_values_template_scripts_and_task_definition_block()
    {
        var config = Config("""{ "ConnectionStrings": { "Db": "Server=s;Password=p1" }, "Smtp": { "Password": "senha2" } }""");
        var plan = SecretsExtractor.Extract(config, "Loja", "Loja.Web");

        var shaped = JsonNode.Parse(SecretsExtractor.AppSettingsShapedJson(plan, null))!;
        Assert.Equal("Server=s;Password=p1", (string)shaped["ConnectionStrings"]!["Db"]!);
        Assert.Equal("senha2", (string)shaped["Smtp"]!["Password"]!);

        var template = JsonNode.Parse(SecretsExtractor.TemplateJson(plan))!;
        Assert.Equal("senha2", (string)template["loja/loja.web/Smtp/Password"]!);

        var bash = SecretsExtractor.CreateScriptBash(plan, "Loja.Web");
        Assert.Contains("aws secretsmanager create-secret", bash);
        Assert.Contains("create \"loja/loja.web/ConnectionStrings/Db\"", bash);
        Assert.DoesNotContain("p1", bash); // values stay in the template, not in the script

        var ps = SecretsExtractor.CreateScriptPowerShell(plan, "Loja.Web");
        Assert.Contains("'loja/loja.web/Smtp/Password'", ps);

        var task = JsonNode.Parse(SecretsExtractor.EcsTaskSecretsJson(plan))!.AsArray();
        Assert.Equal(2, task.Count);
        Assert.Equal("ConnectionStrings__Db", (string)task[0]!["name"]!);
        Assert.Contains("secret:loja/loja.web/ConnectionStrings/Db", (string)task[0]!["valueFrom"]!);

        var userSecrets = SecretsExtractor.UserSecretsScript(plan, "Loja.Web/Loja.Web.csproj");
        Assert.Contains("dotnet user-secrets set \"ConnectionStrings:Db\" 'Server=s;Password=p1'", userSecrets);

        Assert.Equal(SecretsExtractor.UserSecretsId("Loja", "Loja.Web"), SecretsExtractor.UserSecretsId("Loja", "Loja.Web"));
        Assert.NotEqual(SecretsExtractor.UserSecretsId("Loja", "Loja.Web"), SecretsExtractor.UserSecretsId("Loja", "Loja.Worker"));
        Assert.True(Guid.TryParse(SecretsExtractor.UserSecretsId("Loja", "Loja.Web"), out _));
    }

    [Fact]
    public void Leaves_files_without_credentials_alone()
    {
        var config = Config("""{ "AppSettings": { "ItensPorPagina": "20", "Smtp.From": "a@b.c" }, "ConnectionStrings": { "Db": "Server=s;Integrated Security=True" } }""");
        var plan = SecretsExtractor.Extract(config, "Loja", "Loja.Web");
        Assert.False(plan.Any);
        Assert.DoesNotContain("<secret:", config.AppSettingsJson);
    }
}
