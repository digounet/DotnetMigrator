using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// CloudFormation in the layout the portfolio's platform team uses: <c>infra/service.yml</c> per application (VPC, subnets,
/// roles and the shared ALB listener arrive as parameters), <c>infra/data.yml</c> for what the application owns (RDS, bucket,
/// queues, FSx, secret names, CodeDeploy artifacts) and <c>infra/{dev,hom,prod}/parameters*.json</c> in the CodePipeline
/// template-configuration shape with every environment-specific value, URLs and e-mails included. With the .NET Framework
/// target (the default) the service is EC2 Windows + CodeDeploy; with .NET 10 it is ECS Fargate (and Lambda when asked).
/// Secrets are referenced by name only; no value ever passes through a template or a parameter file.
/// </summary>
public static partial class CloudFormationGenerator
{
    public const string InfraDir = "infra";
    /// <summary>Where the migrated solution lives in the repository (the platform pipeline's working-directory).</summary>
    public const string SourceDir = "app/src";
    public const string CodeDeployDir = "infra/codedeploy";

    /// <summary>Deployment environments the parameter folders are generated for.</summary>
    internal static readonly string[] Environments = ["dev", "hom", "prod"];

    private sealed record Deployable(ProjectResult Result, ApplicationProfile Profile, string Slug, string Id, string Logical);

    private sealed record Template(string File, string Stack, string ParametersFile, Deployable? Deployable);

    public static Dictionary<string, string> Generate(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var arch = result.Architecture;
        if (arch == null) return files;
        var components = arch.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var app = Slug(result.SolutionName);
        var framework = result.Options.KeepsFramework;
        var feature = Feature(result);
        var deployables = profiles
            .Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath != null)
            .Select(p => new Deployable(p.Result, p.Profile, Slug(p.Result.Project.Name), Id(p.Result.Project.Name), Logical(p.Result.Project.Name)))
            .ToList();
        if (deployables.Count == 0) return files;
        var lambdas = framework ? [] : deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.Lambda).ToList();
        var workers = framework ? [] : deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker).ToList();
        var services = deployables.Where(d => d.Result.Hosting!.Primary != AwsHosting.Lambda).ToList();
        var fileAutomations = lambdas.Where(l => FileDriven(l.Profile)).ToList();
        var hasDb = result.Databases.Count > 0;
        var hasS3 = components.Contains("s3") || fileAutomations.Count > 0;
        var hasFsx = framework && components.Contains("fsx");
        var secretNames = SecretNames(app, deployables, framework);
        var hasData = hasDb || hasS3 || hasFsx || workers.Count > 0 || secretNames.Count > 0 || framework;
        var notGenerated = profiles.Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath == null).Select(p => p.Result).ToList();
        var templates = new List<Template>();

        if (hasData)
        {
            files[$"{InfraDir}/data.yml"] = DataTemplate(result, hasDb, hasS3, hasFsx, framework, fileAutomations, workers, secretNames);
            templates.Add(new("data.yml", $"{feature}-${{ENV}}-data", "parameters-data.json", null));
        }
        var first = true;
        foreach (var d in services)
        {
            var micro = Micro(result, d);
            var file = first ? "service.yml" : $"service-{micro}.yml";
            var parameters = first ? "parameters.json" : $"parameters-{micro}.json";
            files[$"{InfraDir}/{file}"] = framework ? Ec2ServiceTemplate(result, d, micro, hasDb, hasS3, hasFsx, components) : ServiceTemplate(result, d, micro, hasDb, hasS3, workers.Contains(d), components);
            templates.Add(new(file, $"{feature}-${{ENV}}-{micro}", parameters, d));
            first = false;
        }
        foreach (var l in lambdas)
        {
            var micro = Micro(result, l);
            files[$"{InfraDir}/lambda-{micro}.yml"] = LambdaTemplate(result, l, micro, hasDb, hasS3);
            templates.Add(new($"lambda-{micro}.yml", $"{feature}-${{ENV}}-{micro}", $"parameters-lambda-{micro}.json", l));
        }
        foreach (var environment in Environments)
        {
            if (hasData) files[$"{InfraDir}/{environment}/parameters-data.json"] = ParametersJson(DataParameters(result, environment, feature, hasFsx));
            foreach (var t in templates.Where(t => t.Deployable != null))
                files[$"{InfraDir}/{environment}/{t.ParametersFile}"] = ParametersJson(t.Deployable!.Result.Hosting!.Primary == AwsHosting.Lambda
                    ? LambdaParameters(result, t.Deployable, environment, feature)
                    : framework ? Ec2Parameters(result, t.Deployable, environment, feature) : ServiceParameters(result, t.Deployable, environment, feature, workers.Contains(t.Deployable)));
        }
        files[$"{InfraDir}/deploy.sh"] = DeployBash(templates);
        files[$"{InfraDir}/deploy.ps1"] = DeployPowerShell(templates);
        if (framework)
            foreach (var d in deployables)
                foreach (var (path, content) in CodeDeployBundle(app, feature, Micro(result, d), d))
                    files[$"{CodeDeployDir}/{Micro(result, d)}/{path}"] = content;
        files[$"{InfraDir}/README.md"] = Readme(result, feature, services, lambdas, notGenerated, hasData, templates, framework, components);
        files[".github/workflows/deploy.yml"] = framework ? WorkflowWindows(result, feature, deployables) : ServiceWorkflow(result, feature, services, lambdas);
        foreach (var (path, content) in PlatformFiles(result, profiles)) files[path] = content;
        return files;
    }

    // ------------------------------------------------------------------ pipeline descriptor and test specs

    /// <summary>Repository files the platform expects regardless of the IaC tool: `.iupipes.yml` and the TAAC specs (two identical buildspecs, dev and hom).</summary>
    public static Dictionary<string, string> PlatformFiles(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var framework = result.Options.KeepsFramework;
        var deployables = profiles
            .Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath != null)
            .Select(p => new Deployable(p.Result, p.Profile, Slug(p.Result.Project.Name), Id(p.Result.Project.Name), Logical(p.Result.Project.Name)))
            .ToList();
        files[".iupipes.yml"] = Iupipes(result, Feature(result), deployables, framework);
        foreach (var environment in new[] { "dev", "hom" }) files[$"tests/testspec-{environment}.yml"] = TestSpec(result, framework);
        return files;
    }

    /// <summary>`.iupipes.yml`: what the platform's pipeline reads (language, build, unit tests, publish, CloudFormation template, accounts, Sonar, Fortify).</summary>
    private static string Iupipes(SolutionResult result, string feature, List<Deployable> deployables, bool framework)
    {
        var sb = new StringBuilder();
        sb.AppendLine("project:");
        sb.AppendLine($"  language: \"{(framework ? "dotnet-framework" : "dotnet-core")}\"{(framework ? " # confirme com a plataforma o valor para .NET Framework (lift-and-shift em EC2)" : "")}");
        sb.AppendLine("build:");
        sb.AppendLine($"  nuget-config-path: \"./{SourceDir}\"");
        sb.AppendLine("  parameters: \"/p:PublishWithAspNetCoreTargetManifest=false /p:RunCodeAnalysis=false\"");
        sb.AppendLine("  restore-parameters: \"\"");
        sb.AppendLine($"  version: \"{(framework ? "4.8.1" : "10.0")}\"");
        sb.AppendLine($"  working-directory: \"./{SourceDir}\"");
        if (!framework) sb.AppendLine("  docker-platform: 'linux/arm64'");
        sb.AppendLine("unit-tests:");
        sb.AppendLine("  directory: \".\"");
        sb.AppendLine("publish:");
        sb.AppendLine("  deploy-only: \"\"");
        sb.AppendLine("  output-dir: \".\"");
        sb.AppendLine("  parameters: \"\"");
        sb.AppendLine("infra:");
        sb.AppendLine("  cloudformation:");
        sb.AppendLine("    aws-owner-contact-email: 'po@empresa.com.br'");
        sb.AppendLine("    aws-tech-team-email: 'time@empresa.com.br'");
        sb.AppendLine("    destroy-dev: \"true\"");
        sb.AppendLine("    destroy-hom: \"true\"");
        sb.AppendLine("    destroy-prod: \"true\"");
        sb.AppendLine("    template-file-path: \"service.yml\"");
        sb.AppendLine("    working-directory: \"infra\"");
        sb.AppendLine("deploy:");
        sb.AppendLine("  aws:");
        foreach (var environment in Environments)
        {
            sb.AppendLine($"    {environment}:");
            sb.AppendLine("      account: \"123456789012\"");
        }
        sb.AppendLine("quality:");
        sb.AppendLine("  sonar:");
        sb.AppendLine("    gate-name: ''");
        sb.AppendLine($"    language: '{(framework ? "dotnet-framework" : "dotnet-core")}'");
        sb.AppendLine($"    nugetConfigPath: './{SourceDir}'");
        sb.AppendLine("    parameters: ''");
        sb.AppendLine($"    working-directory: './{SourceDir}'");
        sb.AppendLine("security:");
        sb.AppendLine("  fortify:");
        sb.AppendLine("    environment-analyse: \"pipelinescan\"");
        sb.AppendLine($"    nome-aws: \"{(deployables.Count > 0 ? Micro(result, deployables[0]) : feature)}\"");
        sb.AppendLine($"    produto-aws: \"{feature}\"");
        sb.AppendLine("    sigla: \"SIGLA\"");
        sb.AppendLine("    sigla-app: \"SIGLA-APP\"");
        return Yaml(sb);
    }

    /// <summary>TAAC spec (CodeBuild buildspec 0.2) the pipeline runs after the deploy; identical for dev and hom as in the platform repos.</summary>
    private static string TestSpec(SolutionResult result, bool framework)
    {
        var sb = new StringBuilder();
        sb.AppendLine("version: 0.2");
        sb.AppendLine();
        sb.AppendLine("phases:");
        sb.AppendLine("  build:");
        sb.AppendLine("    commands:");
        sb.AppendLine("      - echo 'Testes executados manualmente'");
        sb.AppendLine($"      # Para automatizar: {(framework ? "smoke test da URL publicada (curl -f https://<host>/) e os testes do projeto " + string.Join(", ", result.Projects.Where(p => p.Project.Kind == ProjectKind.Test).Select(p => p.Project.Name).DefaultIfEmpty("de testes")) + " via vstest" : "dotnet test " + SourceDir + " e um smoke test da URL publicada (curl -f https://<host>/health)")}");
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ naming

    /// <summary>FeatureName: the solution, letters only (AllowedPattern "[a-z]*").</summary>
    internal static string Feature(SolutionResult result) => Letters(result.SolutionName) is { Length: > 0 } f ? f : "app";

    /// <summary>MicroServiceName: the project without the solution prefix, letters only ("LegacyShop.Web" → "web").</summary>
    private static string Micro(SolutionResult result, Deployable d)
    {
        var name = d.Result.Project.Name;
        if (name.StartsWith(result.SolutionName + ".", StringComparison.OrdinalIgnoreCase)) name = name[(result.SolutionName.Length + 1)..];
        var micro = Letters(name);
        if (micro.Length == 0 || micro == Feature(result)) micro = Letters(d.Result.Project.Name);
        return micro.Length == 0 ? "service" : micro;
    }

    private static string Letters(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z]", "");

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');

    private static string Id(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');

    /// <summary>CloudFormation logical IDs are alphanumeric: "LegacyShop.Web" → "LegacyShopWeb".</summary>
    internal static string Logical(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    private static string Yaml(StringBuilder sb) => sb.ToString().Replace("\r\n", "\n");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd('-');

    private const string SnsTopic = "{{resolve:ssm:/org/member/workload_local_sns_arn:1}}";

    private const string PipelineBlock = """
          ### NÃO ALTERE OS PARÂMETROS FEATURENAME, MICROSERVICENAME E DEVTOOLSACCOUNT: USADOS PELAS AUTOMAÇÕES DA PIPELINE ###
          FeatureName:
            Description: Nome da feature (aplicação), só letras minúsculas.
            Type: String
            AllowedPattern: "[a-z]*"
          MicroServiceName:
            Description: Nome do micro serviço, só letras minúsculas.
            Type: String
            AllowedPattern: "[a-z]*"
          DevToolsAccount:
            Description: Conta AWS das ferramentas da esteira (ECR das imagens / artefatos do deploy).
            Type: Number
          ##########################################################################################################
        """;

    // ------------------------------------------------------------------ settings (URLs/e-mails) and secrets

    private static bool FileDriven(ApplicationProfile p) => p.HasAny(Signal.FileWatcher, Signal.Ftp, Signal.MailboxReading) || (p.HasAny(Signal.FileSystemWrites, Signal.UncPaths, Signal.WindowsPaths) && p.Has(Signal.SpreadsheetFiles)) || !p.HasAny(Signal.Msmq, Signal.RabbitMq, Signal.MessageBusFramework, Signal.Kafka, Signal.AzureServiceBus);

    /// <summary>Config transforms name environments the .NET way; map them to the deploy environments.</summary>
    private static string SettingValue(ExternalizedSetting setting, string environment) => environment switch
    {
        "dev" => setting.ValueFor("Development"),
        "prod" => setting.ValueFor("Production"),
        _ => new[] { "Staging", "Homolog", "Homologacao", "Hom", "Hml", "Test" }.Select(setting.ValueFor).FirstOrDefault(v => v != setting.Value) ?? setting.Value
    };

    /// <summary>URLs/e-mails (never secrets) the given deployables need, one parameter per key, with the projects that use it.</summary>
    private static List<(ExternalizedSetting Setting, string Parameter, List<Deployable> UsedBy)> SettingsFor(IEnumerable<Deployable> deployables) =>
        deployables.SelectMany(d => d.Result.SettingsWithDependencies.Where(s => s.Kind != SettingKind.Secret).Select(s => (Setting: s, Deployable: d)))
            .GroupBy(x => x.Setting.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Setting, Logical(g.First().Setting.ParameterPath), g.Select(x => x.Deployable).Distinct().ToList()))
            .OrderBy(x => x.Item2, StringComparer.Ordinal)
            .ToList();

    private static void SettingsParameters(StringBuilder sb, List<(ExternalizedSetting Setting, string Parameter, List<Deployable> UsedBy)> settings, string delivery)
    {
        if (settings.Count == 0) return;
        sb.AppendLine($"  # Valores que estavam fixos no código/appSettings: um por ambiente em <ambiente>/parameters.json; {delivery}.");
        foreach (var (setting, parameter, _) in settings)
        {
            sb.AppendLine($"  {parameter}:");
            sb.AppendLine($"    Description: \"{(setting.Kind == SettingKind.Url ? "URL" : "E-mail")} {setting.Key} ({(setting.Source == SettingSource.Code ? "estava fixo no código" : "appSettings")})\"");
            sb.AppendLine("    Type: String");
            sb.AppendLine($"    Default: \"{setting.Value.Replace("\"", "'")}\"");
        }
    }

    private static List<ExtractedSecret> SecretsFor(Deployable d) =>
        d.Result.Secrets?.Secrets.Where(s => s.Environment is null or "Production").DistinctBy(s => s.EnvironmentVariable).ToList() ?? [];

    private static List<(string Name, string Description)> SecretNames(string app, List<Deployable> deployables, bool framework)
    {
        var names = new List<(string, string)>();
        foreach (var d in deployables)
        {
            if (framework) names.Add(($"{app}/{d.Slug}/config", $"JSON chave→valor gravado no config de {d.Result.Project.Name} pelo CodeDeploy (ConnectionStrings:Nome, AppSettings:Chave)."));
            foreach (var s in SecretsFor(d)) names.Add((s.SecretName, $"{s.ConfigPath} de {d.Result.Project.Name}; valor via _secrets/{d.Result.Project.Name}/create-secrets.sh."));
        }
        return names.DistinctBy(n => n.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string DefaultEngine(SolutionResult result) => result.Databases.Select(d => d.Provider).FirstOrDefault() switch
    {
        "Oracle" => "oracle-se2", "MySQL" => "mysql", "PostgreSQL" => "postgres", _ => "sqlserver-ex"
    };

    private static int DefaultPort(string engine) => engine.StartsWith("sqlserver", StringComparison.Ordinal) ? 1433 : engine.StartsWith("oracle", StringComparison.Ordinal) ? 1521 : engine == "mysql" ? 3306 : 5432;

    // ------------------------------------------------------------------ <env>/parameters*.json, deploy scripts, README

    /// <summary>CodePipeline template-configuration shape, accepted by `aws cloudformation deploy --parameter-overrides file://`.</summary>
    internal static string ParametersJson(List<(string Key, string Value)> parameters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("    \"Parameters\": {");
        for (var i = 0; i < parameters.Count; i++)
            sb.AppendLine($"        \"{parameters[i].Key}\": \"{parameters[i].Value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"{(i < parameters.Count - 1 ? "," : "")}");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return Yaml(sb);
    }

    private static List<(string Key, string Value)> DataParameters(SolutionResult result, string environment, string feature, bool hasFsx)
    {
        var prod = environment == "prod";
        var list = new List<(string, string)> { ("FeatureName", feature), ("Environment", environment) };
        if (result.Databases.Count > 0 || hasFsx)
        {
            list.Add(("VPCID", "vpc-xxxxxxxxxxxxxxxxx")); list.Add(("PrivateSubnetOne", "subnet-xxxxxxxxxxxxxxxx1")); list.Add(("PrivateSubnetTwo", "subnet-xxxxxxxxxxxxxxxx2")); list.Add(("PrivateSubnetThree", "subnet-xxxxxxxxxxxxxxxx3"));
        }
        if (result.Databases.Count > 0)
        {
            var engine = DefaultEngine(result);
            list.Add(("DbEngine", engine));
            list.Add(("DbInstanceClass", engine.StartsWith("sqlserver", StringComparison.Ordinal) ? (prod ? "db.t3.large" : "db.t3.small") : (prod ? "db.t4g.large" : "db.t4g.medium")));
        }
        if (hasFsx) list.Add(("ActiveDirectoryId", ""));
        return list;
    }

    private static string DeployBash(List<Template> templates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# Cria/atualiza as stacks da aplicação na ordem certa. Uso: ./deploy.sh [dev|hom|prod] [região]  (padrão: dev sa-east-1)");
        sb.AppendLine("# Os valores de cada ambiente estão em <ambiente>/parameters*.json (formato {\"Parameters\": {...}}); nada ali é segredo. Gerado pelo Migrator.");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine("cd \"$(dirname \"$0\")\"");
        sb.AppendLine("ENV=\"${1:-dev}\"");
        sb.AppendLine("REGION=\"${2:-sa-east-1}\"");
        sb.AppendLine();
        sb.AppendLine("deploy() {");
        sb.AppendLine("  local template=\"$1\" stack=\"$2\" parameters=\"$3\"");
        sb.AppendLine("  echo \"==> $stack ($template, $ENV/$parameters)\"");
        sb.AppendLine("  aws cloudformation deploy --region \"$REGION\" --template-file \"$template\" --stack-name \"$stack\" \\");
        sb.AppendLine("    --parameter-overrides \"file://$ENV/$parameters\" --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var t in templates) sb.AppendLine($"deploy {t.File} \"{t.Stack.Replace("${ENV}", "$ENV")}\" {t.ParametersFile}");
        return Yaml(sb);
    }

    private static string DeployPowerShell(List<Template> templates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cria/atualiza as stacks da aplicação na ordem certa. Uso: .\\deploy.ps1 [-Environment dev|hom|prod] [-Region sa-east-1]");
        sb.AppendLine("param([string]$Environment = \"dev\", [string]$Region = \"sa-east-1\")");
        sb.AppendLine("$ErrorActionPreference = \"Stop\"");
        sb.AppendLine("Set-Location $PSScriptRoot");
        sb.AppendLine("function Deploy-Stack([string]$Template, [string]$Stack, [string]$Parameters) {");
        sb.AppendLine("  Write-Host \"==> $Stack ($Template, $Environment/$Parameters)\"");
        sb.AppendLine("  aws cloudformation deploy --region $Region --template-file $Template --stack-name $Stack --parameter-overrides \"file://$Environment/$Parameters\" --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw \"Falha na stack $Stack\" }");
        sb.AppendLine("}");
        foreach (var t in templates) sb.AppendLine($"Deploy-Stack \"{t.File}\" \"{t.Stack.Replace("${ENV}", "$Environment")}\" \"{t.ParametersFile}\"");
        return Yaml(sb);
    }

    private static string Readme(SolutionResult result, string feature, List<Deployable> services, List<Deployable> lambdas, List<ProjectResult> notGenerated, bool hasData, List<Template> templates, bool framework, HashSet<string> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Infraestrutura de {result.SolutionName} na AWS (CloudFormation, um serviço por template)");
        sb.AppendLine();
        sb.AppendLine(framework
            ? "Gerado pelo Migrator (destino .NET Framework 4.8.1, lift-and-shift) no padrão da plataforma: `service.yml` descreve só a aplicação (launch template e Auto Scaling group de EC2 Windows, target group e regra no ALB compartilhado, CodeDeploy, log group e alarmes); VPC, subnets, roles e o listener do ALB chegam como parâmetros. Cada ambiente tem a sua pasta (`dev/`, `hom/`, `prod/`) com `parameters*.json` no formato `{\"Parameters\": {...}}`, e é lá que ficam os valores que mudam por ambiente, inclusive as URLs e e-mails que estavam fixos no código. Segredos só por nome (Secrets Manager); nenhum valor sensível passa por template ou parâmetro."
            : "Gerado pelo Migrator (destino .NET 10) no padrão da plataforma: `service.yml` descreve só o serviço ECS (log group, filtro de métrica e alarme, security group, task definition, service e auto scaling); VPC, subnets, cluster, roles e o listener do ALB compartilhado chegam como parâmetros. Cada ambiente tem a sua pasta (`dev/`, `hom/`, `prod/`) com `parameters*.json` no formato `{\"Parameters\": {...}}`, e é lá que ficam os valores que mudam por ambiente, inclusive as URLs e e-mails que estavam fixos no código. Segredos só por nome (Secrets Manager); nenhum valor sensível passa por template ou parâmetro.");
        sb.AppendLine();
        sb.AppendLine("## Arquivos");
        sb.AppendLine();
        foreach (var t in templates)
            sb.AppendLine($"- `{t.File}` → stack `{t.Stack.Replace("${ENV}", "<env>")}`, parâmetros em `<env>/{t.ParametersFile}`: " + (t.Deployable == null
                ? "recursos próprios da aplicação (" + string.Join(", ", new[] { result.Databases.Count > 0 ? "RDS" : null, components.Contains("s3") ? "bucket S3" : null, components.Contains("fsx") && framework ? "FSx for Windows (exige ActiveDirectoryId)" : null, framework ? "bucket de artefatos do CodeDeploy" : null, "nomes dos segredos" }.Where(x => x != null)) + "), exportados para os serviços"
                : $"{t.Deployable.Result.Project.Name} → {t.Deployable.Result.Hosting!.Primary.Display()}"));
        foreach (var missing in notGenerated) sb.AppendLine($"- **{missing.Project.Name}** → {missing.Hosting!.Primary.Display()}: não gerado ({(missing.Project.IsVisualBasic ? "projeto VB.NET não convertido; converta e rode o Migrator de novo" : "projeto sem saída migrada")}).");
        if (framework) sb.AppendLine("- `codedeploy/<microservico>/`: `appspec.yml` e scripts PowerShell (site IIS/app pool, serviço Windows ou tarefa agendada; `after-install.ps1` lê o Parameter Store e os segredos e grava no web.config/app.config da instância).");
        sb.AppendLine(framework
            ? "- `.github/workflows/deploy.yml`: MSBuild em runner Windows, pacote no bucket de artefatos e `aws deploy create-deployment` (referência dos comandos se a plataforma usar a própria esteira)."
            : "- `.github/workflows/deploy.yml`: build, testes, imagem no ECR (`<feature>-<microservico>`, buildx `linux/arm64`) e `update-service`; `dotnet lambda deploy-function` para as Lambdas.");
        sb.AppendLine();
        sb.AppendLine("## Convenções (não altere sem combinar com a plataforma)");
        sb.AppendLine();
        sb.AppendLine($"- `FeatureName` = `{feature}` e `MicroServiceName` = nome do projeto sem o prefixo da solução, só letras minúsculas (AllowedPattern `[a-z]*`); a pipeline usa os dois para nomear {(framework ? "aplicação do CodeDeploy, Auto Scaling group, log group" : "ECR, log group, cluster (`ecs-cluster-<feature>-fargate`) e serviço")} (`<feature>-<env>-<microservico>`).");
        sb.AppendLine(framework
            ? "- `DevToolsAccount` é a conta da esteira; `InstanceProfileArn`/`CodeDeployRoleArn` vazios fazem a stack criar as roles (CAPABILITY_NAMED_IAM), preenchidos usam as roles da plataforma."
            : "- `DevToolsAccount` é a conta do ECR; `ExecutionRoleArn`/`TaskExecutionRoleArn` são as roles fornecidas pela plataforma (a execution role precisa de `secretsmanager:GetSecretValue` no prefixo da aplicação).");
        sb.AppendLine(framework
            ? "- Parâmetros de aplicação (`Urls*`, `Emails*`) viram parâmetros do Parameter Store `/<feature>/<env>/...`, que o `after-install.ps1` grava no appSettings da instância; senhas vêm dos segredos `<app>/<projeto>/config` e dos individuais criados por `_secrets/`."
            : "- Parâmetros de aplicação (`Urls*`, `Emails*`) viram variáveis de ambiente `AppSettings__Secao__Chave`, que o `IConfiguration` lê sem código extra.");
        sb.AppendLine($"- Alarmes publicam no tópico `{SnsTopic}`; ajuste se a conta usar outro nome.");
        sb.AppendLine();
        sb.AppendLine("## Ordem de execução");
        sb.AppendLine();
        var step = 1;
        sb.AppendLine($"{step++}. Preencha `<env>/parameters*.json` com VPC, subnets, roles, listener do ALB e conta da esteira do ambiente; revise as URLs/e-mails por ambiente (os `Web.*.config` já alimentaram o que conheciam).");
        if (hasData) sb.AppendLine($"{step++}. `./deploy.sh dev` cria `data.yml` primeiro (nomes dos segredos{(result.Databases.Count > 0 ? ", RDS" : "")}{(components.Contains("s3") ? ", bucket" : "")}{(framework ? ", bucket de artefatos" : "")}); em seguida rode `_secrets/<projeto>/create-secrets.sh` para colocar os valores nos segredos (o template cria com `PREENCHER`){(framework ? " e preencha o segredo `<app>/<projeto>/config` com o que mais o config precisar" : "")}.");
        sb.AppendLine(framework
            ? $"{step++}. Rode `./deploy.sh <env>` para criar as instâncias (o user data instala IIS/.NET 4.8.1/agentes) e publique pela esteira: MSBuild, zip com `codedeploy/<microservico>/`, `aws deploy push` + `create-deployment` (comandos no workflow)."
            : $"{step++}. Publique a imagem (workflow ou `docker buildx build --platform linux/arm64 -f <proj>/Dockerfile -t <conta>.dkr.ecr.<região>.amazonaws.com/<feature>-<microservico>:<tag> --push .`) e rode `./deploy.sh <env>` para criar/atualizar o serviço com a `ImageTag`.");
        if (lambdas.Count > 0) sb.AppendLine($"{step++}. Lambdas: `dotnet lambda package` + `aws s3 cp` do .zip para `PackageBucket`/`PackageKey`; o deploy da stack cria a função e o workflow atualiza o código depois.");
        if (result.Databases.Count > 0) sb.AppendLine($"{step++}. Dados: restaure o backup no RDS (`.bak` no S3 + `rds_restore_database`, ou AWS DMS), crie o usuário da aplicação e atualize o segredo da connection string.");
        if (framework && components.Contains("fsx")) sb.AppendLine($"{step++}. Arquivos: informe `ActiveDirectoryId` em `<env>/parameters-data.json`, monte `\\\\<FsxDnsName>\\share` com os mesmos nomes de pasta e copie com robocopy/DataSync; ou configure um File Gateway sobre o bucket.");
        sb.AppendLine($"{step}. DNS: aponte o host (`ListenerRuleHost`) para o ALB compartilhado (Route 53 alias).");
        sb.AppendLine();
        sb.AppendLine("## Antes de produção");
        sb.AppendLine();
        sb.AppendLine(framework ? "- `DesiredCapacity >= 2` para as aplicações web em `prod/parameters.json`; AMI do EC2 Image Builder no lugar do user data; restringir o ingress do security group ao security group do ALB." : "- `CapacityProvider: FARGATE` e `DesiredNumberOfTasks >= 2` em `prod/parameters.json`; restringir o ingress do security group ao security group do ALB.");
        sb.AppendLine("- Ajustar o `FilterPattern` do alarme de erros ao formato de log da aplicação.");
        sb.AppendLine("- Validar os templates com `cfn-lint infra/*.yml` após qualquer edição.");
        return Yaml(sb);
    }
}
