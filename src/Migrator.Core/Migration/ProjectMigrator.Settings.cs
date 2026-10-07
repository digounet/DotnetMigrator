using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// Externalized settings: literals rewritten by <see cref="LiteralExternalizer"/> get their keys in the generated
/// appsettings.json (.NET 10) or in the copied app/web.config (.NET Framework); credentials go to the secrets plan with a
/// placeholder in the config, exactly like the ones extracted from config files.
/// </summary>
public static partial class ProjectMigrator
{
    private static LiteralExternalizer.Session ExternalizeLiterals(ProjectInfo project, List<Entry> codeEntries, ProjectResult result, ProjectMigrationContext ctx)
    {
        var session = new LiteralExternalizer.Session();
        if (project.IsVisualBasic) return session;
        foreach (var entry in codeEntries)
        {
            var rewritten = LiteralExternalizer.Externalize(entry.Text!, entry.Unix, session);
            if (!ReferenceEquals(rewritten, entry.Text) && rewritten != entry.Text) entry.Text = rewritten;
        }
        ReportExternalized(project, session, result, ctx);
        return session;
    }

    /// <summary>Inventory items and the settings list (secrets get their Secrets Manager name here).</summary>
    private static void ReportExternalized(ProjectInfo project, LiteralExternalizer.Session session, ProjectResult result, ProjectMigrationContext ctx)
    {
        var items = result.Inventory;
        var rewritten = session.Literals.Where(l => l.Rewritten).ToList();
        foreach (var literal in rewritten.DistinctBy(l => l.Key))
        {
            var configPath = LiteralExternalizer.ConfigPath(literal.Key);
            var secret = literal.Kind == SettingKind.Secret ? SecretsExtractor.ForCode(ctx.SolutionName, project.Name, configPath, literal.Value) : null;
            result.Settings.Add(new ExternalizedSetting
            {
                Project = project.Name, Kind = literal.Kind, Key = configPath, Value = literal.Value, Source = SettingSource.Code,
                Location = $"{literal.File}:{literal.Line}", SecretName = secret?.SecretName
            });
        }
        var config = rewritten.Where(l => l.Kind != SettingKind.Secret).ToList();
        if (config.Count > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Code, "CS-CONFIG-EXTERNALIZED",
                $"{config.Count} valor(es) fixos no código viraram configuração: {string.Join(", ", config.Select(l => l.Key).Distinct().Take(8))}",
                $"URLs e e-mails fixos em literais ({string.Join(", ", config.Select(l => $"{l.File}:{l.Line}").Take(6))}) foram trocados por leitura de configuração; a chave e o valor atual estão no {(ctx.Target == MigrationTarget.NetFramework ? "app/web.config" : "appsettings.json")} e viram parâmetros da infraestrutura (um valor por ambiente). Campos const passaram a static readonly.",
                ctx.Target == MigrationTarget.NetFramework
                    ? "Confira os valores por ambiente em infra/cloudformation/parameters/*.json; a instância recebe os valores pelo Parameter Store."
                    : "Onde a classe não recebe IConfiguration (classes estáticas, helpers), o build de verificação aponta o ponto: injete IConfiguration ou leia no startup. Os valores por ambiente ficam nos parâmetros da infraestrutura.",
                config.Count == 1 ? config[0].File : null, auto: true));
        var secrets = rewritten.Where(l => l.Kind == SettingKind.Secret).ToList();
        if (secrets.Count > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Code, "CS-SECRET-EXTERNALIZED",
                $"{secrets.Count} credencial(is) fixas no código movidas para {SecretsExtractor.RootFolder}/: {string.Join(", ", secrets.Select(l => l.Key).Distinct().Take(6))}",
                $"Os literais ({string.Join(", ", secrets.Select(l => $"{l.File}:{l.Line}").Take(6))}) viraram leitura de configuração com marcador '<secret: nome>'; os valores reais e os scripts do Secrets Manager estão em {SecretsExtractor.RootFolder}/{project.Name}/ (fora do git e do Docker).",
                "Rode create-secrets.sh e rotacione as credenciais: elas continuam no histórico do repositório original.", secrets.Count == 1 ? secrets[0].File : null, auto: true));
        var skipped = session.Literals.Where(l => !l.Rewritten).ToList();
        if (skipped.Count > 0)
            items.Add(Item(project, skipped.Any(l => l.Kind == SettingKind.Secret) ? InventorySeverity.Warning : InventorySeverity.Info, InventoryCategory.Code, "CS-CONFIG-SKIPPED",
                $"{skipped.Count} valor(es) fixos não puderam ser externalizados automaticamente",
                string.Join("; ", skipped.Take(8).Select(l => $"{l.File}:{l.Line} ({l.SkipReason}): {Mask(l)}")),
                "Atributos, rótulos de switch, valores padrão de parâmetro e strings interpoladas/verbatim exigem valor em tempo de compilação ou montagem: mova para configuração manualmente (ex.: montar a URL a partir de uma base configurável)."));
        foreach (var m in result.Modernizations.Where(m => m.RuleId == "MOD-SEC-SECRETS-CODE" && secrets.Count > 0))
            m.Why += $" O Migrator trocou {secrets.Count} literal(is) por leitura de configuração e moveu os valores para {SecretsExtractor.RootFolder}/ (item CS-SECRET-EXTERNALIZED); o que restou está em CS-CONFIG-SKIPPED.";
        foreach (var m in result.Modernizations.Where(m => m.RuleId == "MOD-ARCH-HARDCODED-URL" && config.Count > 0))
            m.Why += $" O Migrator externalizou {config.Count} valor(es) para configuração (item CS-CONFIG-EXTERNALIZED), que viram parâmetros por ambiente na infraestrutura.";
    }

    private static string Mask(ExternalizedLiteral l) => l.Kind == SettingKind.Secret ? "***" : l.Value;

    /// <summary>.NET 10: keys land in the generated appsettings.json (AppSettings section); secrets as placeholders + plan.</summary>
    private static void AddExternalizedToConfig(ProjectInfo project, LiteralExternalizer.Session session, ConfigMigrationResult config, SecretsPlan secrets, ProjectResult result, ProjectMigrationContext ctx)
    {
        var rewritten = session.Literals.Where(l => l.Rewritten).DistinctBy(l => l.Key).ToList();
        if (rewritten.Count == 0) return;
        JsonObject root;
        try { root = JsonNode.Parse(config.AppSettingsJson ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { root = new JsonObject(); }
        foreach (var literal in rewritten)
        {
            var configPath = LiteralExternalizer.ConfigPath(literal.Key);
            string value;
            if (literal.Kind == SettingKind.Secret && !ctx.KeepSecrets)
            {
                var secret = SecretsExtractor.ForCode(ctx.SolutionName, project.Name, configPath, literal.Value);
                secrets.Secrets.Add(secret);
                value = $"{SecretsExtractor.PlaceholderPrefix}{secret.SecretName}>";
            }
            else value = literal.Value;
            Set(root, configPath, value);
        }
        config.AppSettingsJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static void Set(JsonObject root, string configPath, string value)
    {
        var parts = configPath.Split(':');
        var current = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (current[parts[i]] is not JsonObject next) current[parts[i]] = next = new JsonObject();
            current = next;
        }
        current[parts[^1]] = value;
    }

    // ---------------------------------------------------------------- .NET Framework target (text edits on the copied config/csproj)

    /// <summary>Adds &lt;add key="Urls:X" value="..."/&gt; entries to the appSettings of the copied config (creating the section when missing).</summary>
    internal static string AddAppSettings(string configXml, IEnumerable<(string Key, string Value)> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return configXml;
        var block = string.Concat(list.Select(e => $"    <add key=\"{Escape(e.Key)}\" value=\"{Escape(e.Value)}\" />\n"));
        var close = Regex.Match(configXml, @"[ \t]*</appSettings>");
        if (close.Success) return configXml.Insert(close.Index, block);
        var selfClosing = Regex.Match(configXml, @"[ \t]*<appSettings\s*/>");
        if (selfClosing.Success) return configXml[..selfClosing.Index] + "  <appSettings>\n" + block + "  </appSettings>" + configXml[(selfClosing.Index + selfClosing.Length)..];
        var open = Regex.Match(configXml, @"<configuration[^>]*>\s*\n?");
        if (!open.Success) return configXml;
        return configXml.Insert(open.Index + open.Length, "  <appSettings>\n" + block + "  </appSettings>\n");
    }

    /// <summary>Old-style projects need the System.Configuration assembly for ConfigurationManager.</summary>
    internal static string EnsureSystemConfigurationReference(string projectXml, bool sdkStyle)
    {
        if (Regex.IsMatch(projectXml, @"<Reference\s+Include=""System\.Configuration[""',]")) return projectXml;
        if (sdkStyle) return projectXml; // net481 SDK-style projects reference the framework assemblies implicitly
        var reference = Regex.Match(projectXml, @"[ \t]*<Reference\s+Include=");
        if (reference.Success) return projectXml.Insert(reference.Index, "    <Reference Include=\"System.Configuration\" />\n");
        var group = Regex.Match(projectXml, @"[ \t]*<ItemGroup>");
        return group.Success ? projectXml.Insert(group.Index, "  <ItemGroup>\n    <Reference Include=\"System.Configuration\" />\n  </ItemGroup>\n") : projectXml;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
