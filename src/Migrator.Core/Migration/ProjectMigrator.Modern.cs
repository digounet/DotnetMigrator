using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// Projects that are already SDK-style on .NET (Core/5+/netstandard), alone or mixed with .NET Framework projects in the
/// same solution: their appsettings*.json and code get the same treatment the legacy ones get from web.config and the
/// rewrites (connection strings and credentials out to Secrets Manager, URLs/e-mails as per-environment parameters,
/// fixed literals turned into configuration reads). Used by the .NET 10 path (after <see cref="ModernProjectUpdater"/>)
/// and by lift-and-shift (where these projects are copied as they are).
/// </summary>
public static partial class ProjectMigrator
{
    public const string SettingsHelperFile = "MigratorSettings.cs";

    /// <summary>Runs the appsettings + literal analysis; returns true when the project now needs the Microsoft.Extensions.Configuration packages (the helper was generated).</summary>
    private static bool ApplyModernConfiguration(ProjectInfo project, ProjectMigrationContext ctx, ProjectResult result, OutputPlan plan, ApplicationProfile profile)
    {
        var items = result.Inventory;
        var files = AppSettingsAnalyzer.Load(project);
        if (files != null)
        {
            AppSettingsAnalyzer.Profile(profile, project, files);
            result.Settings.AddRange(AppSettingsAnalyzer.Settings(project, files));
        }

        // Fixed URLs/e-mails/credentials in code → MigratorSettings.Get("AppSettings:...") (appsettings + environment variables, as the host reads them).
        var session = new LiteralExternalizer.Session();
        var rewrittenFiles = 0;
        if (!project.IsVisualBasic)
            foreach (var file in project.SourceFiles.Where(f => File.Exists(f.FullPath) && f.IsInside(project.ProjectDir)))
            {
                var relative = Path.GetRelativePath(project.ProjectDir, file.FullPath).Replace('\\', '/');
                if (relative.EndsWith(SettingsHelperFile, StringComparison.OrdinalIgnoreCase)) continue;
                var (text, _) = TextFiles.Read(file.FullPath);
                var rewritten = LiteralExternalizer.Externalize(text, relative, session, LiteralExternalizer.ReadStyle.MigratorSettings);
                if (rewritten == text) continue;
                plan.Write(Path.Combine(result.RelativeDir, relative), rewritten);
                rewrittenFiles++;
            }
        ReportExternalized(project, session, result, ctx, modern: true);

        var config = files?.Config ?? new ConfigMigrationResult();
        var hasRewrites = session.Literals.Any(l => l.Rewritten);
        if (hasRewrites && config.AppSettingsJson == null && project.Kind != ProjectKind.ClassLibrary) config.AppSettingsJson = "{}";
        var secrets = ctx.KeepSecrets ? new SecretsPlan() : SecretsExtractor.Extract(config, ctx.SolutionName, project.Name);
        if (config.AppSettingsJson != null) AddExternalizedToConfig(project, session, config, secrets, result, ctx);
        else if (hasRewrites)
        {
            // Libraries have no appsettings of their own: the keys must exist in the host's appsettings (the IaC parameters already cover the deployables that ship it).
            foreach (var literal in session.Literals.Where(l => l.Rewritten && l.Kind == SettingKind.Secret && !ctx.KeepSecrets).DistinctBy(l => l.Key))
                secrets.Secrets.Add(SecretsExtractor.ForCode(ctx.SolutionName, project.Name, LiteralExternalizer.ConfigPath(literal.Key), literal.Value));
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Configuration, "CFG-MODERN-LIBRARY",
                "Biblioteca: as chaves externalizadas precisam estar no appsettings da aplicação que a usa",
                $"MigratorSettings.Get lê appsettings.json/variáveis de ambiente do processo host; chaves: {string.Join(", ", session.Literals.Where(l => l.Rewritten).Select(l => LiteralExternalizer.ConfigPath(l.Key)).Distinct().Take(8))}.",
                "Acrescente as chaves ao appsettings.json dos projetos executáveis que referenciam esta biblioteca (os parâmetros da infraestrutura já incluem os valores por ambiente)."));
        }

        // The scrubbed appsettings*.json replace the copied originals (writes are applied after copies).
        if (files != null || config.AppSettingsJson != null)
        {
            if (config.AppSettingsJson != null) plan.Write(Path.Combine(result.RelativeDir, files?.BaseFile ?? "appsettings.json"), config.AppSettingsJson);
            foreach (var (environment, json) in config.EnvironmentJson) plan.Write(Path.Combine(result.RelativeDir, $"appsettings.{environment}.json"), json);
        }
        if (secrets.Any)
        {
            result.Secrets = secrets;
            WriteSecretsArtifacts(project, secrets, plan, result.RelativeDir, items);
            foreach (var m in result.Modernizations.Where(m => m.RuleId == "MOD-SEC-SECRETS"))
                m.Why = m.Why.Replace("foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório.",
                    $"foram retiradas do appsettings.json e ficaram em {SecretsExtractor.RootFolder}/{project.Name}/ (fora do repositório e da imagem).");
        }
        if (files != null)
        {
            var (cs, secretCount, endpoints, unc) = AppSettingsAnalyzer.Summary(files);
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Configuration, "CFG-APPSETTINGS",
                $"{files.BaseFile} analisado: {cs} connection string(s), {secretCount} credencial(is), {endpoints} URL(s)/e-mail(s), {unc} pasta(s) de rede",
                $"Connection strings viram bancos na arquitetura (RDS) e, com senha, segredos; chaves com nome de credencial viram segredos; URLs e e-mails viram parâmetros com um valor por ambiente ({(files.EnvironmentFiles.Count > 0 ? string.Join(", ", files.EnvironmentFiles.Keys) : "sem appsettings.<Ambiente>.json")}); pastas de rede entram na hospedagem (FSx/S3).",
                secretCount > 0 && !ctx.KeepSecrets ? "Os valores dos segredos estão em _secrets/ (fora do git): rode create-secrets.sh e rotacione as credenciais." : "Nenhuma ação necessária.",
                files.BaseFile, auto: true));
        }
        if (hasRewrites)
        {
            plan.Write(Path.Combine(result.RelativeDir, SettingsHelperFile), SettingsHelper(project));
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Code, "CS-CONFIG-HELPER",
                $"{SettingsHelperFile} gerado: leitura de configuração para os valores externalizados",
                "Classe estática que monta IConfiguration (appsettings.json + appsettings.<Ambiente>.json + variáveis de ambiente) uma vez; as leituras MigratorSettings.Get(\"AppSettings:...\") substituem os literais. Pacotes Microsoft.Extensions.Configuration.Json/EnvironmentVariables adicionados ao projeto.",
                "Se o projeto já injeta IConfiguration/IOptions, troque as chamadas pelo mecanismo existente; o helper existe para não exigir mudança de assinatura nas classes.", SettingsHelperFile, auto: true));
        }
        return hasRewrites;
    }

    /// <summary>The generated helper: block-scoped namespace and no nullable annotations so it compiles on any LangVersion (netstandard2.0 included).</summary>
    internal static string SettingsHelper(ProjectInfo project)
    {
        var ns = string.IsNullOrWhiteSpace(project.RootNamespace) ? Regex.Replace(project.Name, @"[^\w.]", "_") : project.RootNamespace;
        return $$"""
            using System;
            using Microsoft.Extensions.Configuration;

            namespace {{ns}}
            {
                /// <summary>
                /// Gerado pelo Migrator: leitura de configuração para os valores que estavam fixos no código.
                /// Lê appsettings.json, appsettings.{Ambiente}.json e variáveis de ambiente (Secao__Chave), como o host da aplicação.
                /// </summary>
                internal static class MigratorSettings
                {
                    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
                        .SetBasePath(AppContext.BaseDirectory)
                        .AddJsonFile("appsettings.json", optional: true)
                        .AddJsonFile("appsettings." + (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production") + ".json", optional: true)
                        .AddEnvironmentVariables()
                        .Build();

                    public static string Get(string key)
                    {
                        var value = Configuration[key];
                        if (string.IsNullOrEmpty(value))
                            throw new InvalidOperationException("Configuração '" + key + "' não encontrada: defina no appsettings.json ou na variável de ambiente " + key.Replace(":", "__") + ".");
                        return value;
                    }
                }
            }

            """.Replace("\r\n", "\n");
    }

    /// <summary>Adds the two configuration packages to an SDK-style project file when nothing already brings them (Hosting, Web SDK, Configuration.*).</summary>
    internal static string EnsureConfigurationPackages(string csproj)
    {
        if (csproj.Contains("Microsoft.Extensions.Configuration.Json", StringComparison.OrdinalIgnoreCase) || csproj.Contains("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase) ||
            csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || csproj.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
            return csproj;
        var tfm = Regex.Match(csproj, @"<TargetFrameworks?>([^<]+)</TargetFrameworks?>").Groups[1].Value;
        var version = tfm.Contains("net10", StringComparison.OrdinalIgnoreCase) ? "10.0.0" : tfm.Contains("net9", StringComparison.OrdinalIgnoreCase) ? "9.0.0" : "8.0.1";
        var group = $"  <ItemGroup>\n    <!-- Migrator: MigratorSettings.cs lê appsettings + variáveis de ambiente -->\n    <PackageReference Include=\"Microsoft.Extensions.Configuration.Json\" Version=\"{version}\" />\n    <PackageReference Include=\"Microsoft.Extensions.Configuration.EnvironmentVariables\" Version=\"{version}\" />\n  </ItemGroup>\n";
        var close = csproj.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        return close < 0 ? csproj : csproj.Insert(close, group);
    }
}
