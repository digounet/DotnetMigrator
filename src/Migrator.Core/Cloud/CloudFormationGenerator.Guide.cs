using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// The deployment guide: the same plan <see cref="Generate"/> renders into templates, described for people (report, repository
/// README, JSON). Built on every run, including analyze, so the team knows what has to be configured before the files exist.
/// </summary>
public static partial class CloudFormationGenerator
{
    /// <summary>What the generator would write: deployables, templates, flags. Shared by Generate and Guide so names never diverge.</summary>
    private sealed record InfraPlan(string App, string Feature, bool Framework, List<Deployable> Deployables, List<Deployable> Services, List<Deployable> Workers, List<Deployable> Lambdas,
        List<Deployable> FileAutomations, bool HasDb, bool HasS3, bool HasFsx, bool HasData, List<(string Name, string Description)> SecretNames, List<ProjectResult> NotGenerated,
        List<Template> Templates, HashSet<string> Components);

    private static InfraPlan? PlanInfra(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var arch = result.Architecture;
        if (arch == null) return null;
        var components = arch.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var app = Slug(result.SolutionName);
        var framework = result.Options.KeepsFramework;
        var feature = Feature(result);
        var deployables = profiles
            .Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath != null)
            .Select(p => new Deployable(p.Result, p.Profile, Slug(p.Result.Project.Name), Id(p.Result.Project.Name), Logical(p.Result.Project.Name)))
            .ToList();
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
        if (deployables.Count > 0 && hasData) templates.Add(new("data.yml", $"{feature}-${{ENV}}-data", "parameters-data.json", null));
        var first = true;
        foreach (var d in services)
        {
            var micro = Micro(result, d);
            templates.Add(new(first ? "service.yml" : $"service-{micro}.yml", $"{feature}-${{ENV}}-{micro}", first ? "parameters.json" : $"parameters-{micro}.json", d));
            first = false;
        }
        foreach (var l in lambdas)
        {
            var micro = Micro(result, l);
            templates.Add(new($"lambda-{micro}.yml", $"{feature}-${{ENV}}-{micro}", $"parameters-lambda-{micro}.json", l));
        }
        return new InfraPlan(app, feature, framework, deployables, services, workers, lambdas, fileAutomations, hasDb, hasS3, hasFsx, hasData, secretNames, notGenerated, templates, components);
    }

    private static List<(string Key, string Value)> ParametersOf(SolutionResult result, InfraPlan plan, Template t, string environment) =>
        t.Deployable == null ? DataParameters(result, environment, plan.Feature, plan.HasFsx)
        : t.Deployable.Result.Hosting!.Primary == AwsHosting.Lambda ? LambdaParameters(result, t.Deployable, environment, plan.Feature)
        : plan.Framework ? Ec2Parameters(result, t.Deployable, environment, plan.Feature)
        : ServiceParameters(result, t.Deployable, environment, plan.Feature, plan.Workers.Contains(t.Deployable));

    /// <summary>Descriptions of the template parameters, as the templates declare them.</summary>
    private static readonly Dictionary<string, (string Group, string Description)> ParameterDocs = new(StringComparer.Ordinal)
    {
        ["FeatureName"] = ("Esteira", "Nome da feature (aplicação), só letras minúsculas; usado pela pipeline para nomear recursos. Não altere."),
        ["MicroServiceName"] = ("Esteira", "Nome do microsserviço (projeto sem o prefixo da solução), só letras minúsculas. Não altere."),
        ["DevToolsAccount"] = ("Esteira", "Conta AWS das ferramentas da esteira (ECR das imagens / artefatos do deploy)."),
        ["Environment"] = ("Esteira", "Ambiente (dev, hom, prod): entra nos nomes dos recursos e nos caminhos do Parameter Store."),
        ["VPCID"] = ("Compartilhada", "ID da VPC da conta, fornecida pela plataforma."),
        ["PrivateSubnetOne"] = ("Compartilhada", "Subnet privada 1 (instâncias/tasks e RDS)."),
        ["PrivateSubnetTwo"] = ("Compartilhada", "Subnet privada 2."),
        ["PrivateSubnetThree"] = ("Compartilhada", "Subnet privada 3."),
        ["InstanceType"] = ("Dimensionamento", "Tipo da instância EC2 Windows."),
        ["InstanceProfileArn"] = ("Compartilhada", "Instance profile da plataforma (SSM, CloudWatch agent, segredos e Parameter Store do prefixo da aplicação, bucket de artefatos). Vazio = a stack cria."),
        ["CodeDeployRoleArn"] = ("Compartilhada", "Service role do CodeDeploy. Vazio = a stack cria."),
        ["DesiredCapacity"] = ("Dimensionamento", "Instâncias desejadas no Auto Scaling group."),
        ["MinCapacity"] = ("Dimensionamento", "Mínimo de instâncias."),
        ["MaxCapacity"] = ("Dimensionamento", "Máximo de instâncias."),
        ["TimeZone"] = ("Dimensionamento", "Fuso horário das instâncias (tzutil), para manter DateTime.Now como on-premises."),
        ["InstancePort"] = ("Dimensionamento", "Porta do site no IIS (target group)."),
        ["LoadBalancerListenerArn"] = ("Compartilhada", "Listener HTTPS do Application Load Balancer compartilhado onde a regra da aplicação é criada."),
        ["ListenerRulePriority"] = ("Compartilhada", "Prioridade da regra no listener (única por aplicação no mesmo ALB)."),
        ["ListenerRulePath"] = ("Aplicação", "Caminho roteado para esta aplicação (\"/*\" quando o host header já a identifica)."),
        ["ListenerRuleHost"] = ("Aplicação", "Host header (DNS) da aplicação; crie o registro no Route 53 apontando para o ALB."),
        ["HealthCheckPath"] = ("Aplicação", "Caminho que responde 200 sem autenticação para o health check do target group."),
        ["DbEngine"] = ("Dados", "Engine do RDS (sqlserver-ex/se/ee, oracle-se2, mysql, postgres)."),
        ["DbInstanceClass"] = ("Dados", "Classe da instância RDS."),
        ["ActiveDirectoryId"] = ("Dados", "ID do AWS Managed Microsoft AD (d-xxxx) exigido pelo FSx for Windows; vazio = não criar o FSx."),
        ["DesiredNumberOfTasks"] = ("Dimensionamento", "Tasks desejadas no serviço ECS."),
        ["MinCapacityTask"] = ("Dimensionamento", "Mínimo de tasks (auto scaling)."),
        ["MaxCapacityTask"] = ("Dimensionamento", "Máximo de tasks (auto scaling)."),
        ["ExposedPortInDockerfile"] = ("Dimensionamento", "Porta exposta no Dockerfile e usada pela aplicação (8080)."),
        ["ListenerContainerPort"] = ("Compartilhada", "Porta TCP deste serviço no NLB compartilhado; não pode colidir com outros serviços da feature."),
        ["ContainerCpu"] = ("Dimensionamento", "vCPU reservada para o container (unidades)."),
        ["ContainerMemory"] = ("Dimensionamento", "Memória (MiB) do container."),
        ["ContainerMemoryReservation"] = ("Dimensionamento", "Reserva mínima de memória (MiB)."),
        ["CapacityProvider"] = ("Dimensionamento", "FARGATE (produção) ou FARGATE_SPOT (ambientes inferiores)."),
        ["WeightFargate"] = ("Dimensionamento", "Peso do capacity provider FARGATE quando há Spot."),
        ["EcsClusterName"] = ("Compartilhada", "Cluster ECS da feature (ecs-cluster-<feature>-fargate), criado pela plataforma."),
        ["Squad"] = ("Tags", "Tag obrigatória: squad responsável."),
        ["Sigla"] = ("Tags", "Tag obrigatória: sigla do projeto."),
        ["Versao"] = ("Tags", "Versão da aplicação (vínculo com o Datadog)."),
        ["TechTeamEmail"] = ("Tags", "Tag obrigatória: e-mail do time técnico."),
        ["OwnerTeamEmail"] = ("Tags", "Tag obrigatória: e-mail do PO."),
        ["GithubRepoId"] = ("Tags", "ID numérico do repositório GitHub (tag REPO_ID)."),
        ["ScheduleExpression"] = ("Aplicação", "Expressão do EventBridge Scheduler (cron(...) ou rate(...)), em UTC."),
        ["LambdaRoleArn"] = ("Compartilhada", "Role da função (logs, VPC, segredos do prefixo, S3/SQS). Vazio = a stack cria."),
        ["LambdaRuntime"] = ("Dimensionamento", "Runtime gerenciado da Lambda."),
        ["PackageBucket"] = ("Esteira", "Bucket com o pacote .zip da função (saída de dotnet lambda package)."),
        ["PackageKey"] = ("Esteira", "Chave do .zip no bucket."),
        ["MailboxPollSchedule"] = ("Aplicação", "Frequência de consulta à caixa postal enquanto o recebimento pelo SES não estiver configurado."),
        ["EnableSesInbound"] = ("Aplicação", "true para receber os e-mails pelo SES (regra de recebimento → S3 → evento)."),
        ["InboundRecipients"] = ("Aplicação", "Endereços/domínios recebidos pelo SES."),
    };

    private static bool IsPlaceholder(string name, string value) =>
        value.Length == 0 || value.Contains("xxxx", StringComparison.OrdinalIgnoreCase) || value.Contains("123456789012", StringComparison.Ordinal) ||
        value.Contains("empresa.com.br", StringComparison.OrdinalIgnoreCase) || value.Contains("exemplo.com", StringComparison.OrdinalIgnoreCase) ||
        name is "Squad" or "Sigla" or "GithubRepoId" or "ActiveDirectoryId" && value is "squad" or "sigla" or "0" or "";

    /// <summary>Builds the guide for the current options (CloudFormation or Terraform). Returns null without an architecture (--cloud none).</summary>
    public static DeploymentGuide? Guide(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var plan = PlanInfra(result, profiles);
        if (plan == null) return null;
        var cloudFormation = result.Options.EffectiveIac == IacTool.CloudFormation;
        var guide = new DeploymentGuide
        {
            Feature = plan.Feature,
            Target = result.Options.Target.Display(),
            Iac = result.Options.EffectiveIac.Display(),
            InfrastructureWritten = !result.Options.DryRun && result.Options.GenerateInfrastructure
        };
        guide.Environments.AddRange(Environments);
        var arch = result.Architecture!;

        // ---------------------------------------------------------------- units
        foreach (var d in plan.Deployables)
        {
            var hosting = d.Result.Hosting!;
            var template = plan.Templates.FirstOrDefault(t => t.Deployable == d);
            var isWeb = d.Result.Project.Kind == ProjectKind.Web;
            var micro = Micro(result, d);
            var unit = new DeploymentUnit
            {
                Project = d.Result.Project.Name, Kind = Reporting.ReportWriter.KindLabel(d.Result.Project), Micro = micro, Hosting = hosting.Primary, HostingLabel = hosting.Primary.Display(),
                Runtime = plan.Framework
                    ? isWeb ? "site no IIS (app pool próprio) em instâncias EC2 Windows com Auto Scaling, atrás do ALB compartilhado; deploy pelo CodeDeploy"
                    : d.Result.Project.Kind == ProjectKind.WindowsService ? "serviço Windows instalado pelo CodeDeploy em EC2 Windows (instância única)"
                    : "tarefa do Agendador de Tarefas (console) em EC2 Windows, criada pelo CodeDeploy"
                    : hosting.Primary switch
                    {
                        AwsHosting.Lambda => "função Lambda (runtime dotnet10) disparada por eventos (S3/SQS/agendamento)",
                        AwsHosting.EcsFargateWorker => "container Linux ARM64 no ECS Fargate consumindo a fila SQS, escalado pela profundidade da fila",
                        AwsHosting.EcsScheduledTask => "container Linux ARM64 executado pelo EventBridge Scheduler (ECS RunTask); o Main() roda e termina",
                        AwsHosting.EcsWindows => "container Windows no ECS (o template gerado é o Fargate Linux da plataforma: ajuste CpuArchitecture/OperatingSystemFamily ou mantenha em EC2 Windows)",
                        _ => "container Linux ARM64 no ECS Fargate com sidecars Datadog/FireLens, exposto no NLB compartilhado"
                    },
                TemplateFile = template?.File, Stack = template?.Stack.Replace("${ENV}", "<env>"), ParametersFile = template?.ParametersFile,
                Endpoint = isWeb ? (plan.Framework ? $"https://{d.Slug}.empresa.com.br (ListenerRuleHost; registre no Route 53)" : "NLB compartilhado, porta ListenerContainerPort (8080 por padrão; uma porta por serviço)") : null,
                HealthCheck = isWeb ? (plan.Framework ? "GET HealthCheckPath (padrão \"/\"); responda 200 sem autenticação" : "GET /health na porta 8080 (gerado no Program.cs)") : null,
                Schedule = hosting.Primary == AwsHosting.EcsScheduledTask ? "ScheduleExpression (rate(15 minutes) por padrão, UTC)" : plan.Framework && d.Result.Project.Kind == ProjectKind.Console ? "gatilho do Agendador de Tarefas definido em codedeploy/<micro>/application-start.ps1 (a cada 15 min por padrão)" : null,
                RequiresWindows = hosting.RequiresWindows
            };
            unit.Prerequisites.AddRange(hosting.Prerequisites);
            guide.Units.Add(unit);
        }
        foreach (var p in plan.NotGenerated)
        {
            var unit = new DeploymentUnit
            {
                Project = p.Project.Name, Kind = Reporting.ReportWriter.KindLabel(p.Project), Micro = MicroOf(result, p.Project.Name), Hosting = p.Hosting!.Primary, HostingLabel = p.Hosting.Primary.Display(),
                Runtime = "—", RequiresWindows = p.Hosting.RequiresWindows,
                NotGenerated = p.Project.IsVisualBasic ? "projeto VB.NET não convertido: converta (Upgrade Assistant/CodeConverter) e rode o Migrator de novo para gerar a infra" : "projeto sem saída migrada"
            };
            unit.Prerequisites.AddRange(p.Hosting.Prerequisites);
            guide.Units.Add(unit);
        }

        // ---------------------------------------------------------------- files, parameters
        if (cloudFormation)
        {
            var order = 1;
            foreach (var t in plan.Templates)
                guide.Files.Add(new InfraFile
                {
                    Path = $"{InfraDir}/{t.File}", Stack = t.Stack.Replace("${ENV}", "<env>"), ParametersFile = $"{InfraDir}/<env>/{t.ParametersFile}", DeployOrder = order++,
                    Purpose = t.Deployable == null
                        ? "recursos próprios da aplicação (" + string.Join(", ", new[] { plan.HasDb ? "RDS" : null, plan.HasS3 ? "bucket S3 + fila de eventos" : null, plan.HasFsx ? "FSx for Windows" : null, plan.Workers.Count > 0 ? "filas dos workers" : null, plan.Framework ? "bucket de artefatos do CodeDeploy" : null, plan.SecretNames.Count > 0 ? "nomes dos segredos" : null }.Where(x => x != null)) + "), exportados para os serviços"
                        : $"{t.Deployable.Result.Project.Name}: {t.Deployable.Result.Hosting!.Primary.Display()}"
                });
            if (plan.Framework)
                foreach (var d in plan.Deployables)
                    guide.Files.Add(new InfraFile { Path = $"{CodeDeployDir}/{Micro(result, d)}/", Purpose = $"appspec.yml + before-install/after-install/application-start/validate-service.ps1 de {d.Result.Project.Name}; after-install.ps1 grava Parameter Store e segredos no config da instância" });
            guide.Files.Add(new InfraFile { Path = $"{InfraDir}/deploy.sh, deploy.ps1", Purpose = "aws cloudformation deploy de cada stack na ordem acima com a pasta do ambiente (./deploy.sh dev|hom|prod [região])" });
            guide.Files.Add(new InfraFile { Path = $"{InfraDir}/README.md", Purpose = "convenções da plataforma, ordem de execução e checklist de produção" });
            guide.Files.Add(new InfraFile { Path = ".github/workflows/deploy.yml", Purpose = plan.Framework ? "MSBuild em runner Windows → zip → CodeDeploy (referência dos comandos se a esteira da plataforma não for usada)" : "buildx linux/arm64 → ECR → update-service (e dotnet lambda deploy-function)" });

            var parameters = new Dictionary<string, InfraParameter>(StringComparer.Ordinal);
            foreach (var t in plan.Templates)
                foreach (var environment in Environments)
                    foreach (var (key, value) in ParametersOf(result, plan, t, environment))
                    {
                        if (!parameters.TryGetValue(key, out var p))
                        {
                            var setting = plan.Deployables.SelectMany(d => d.Result.SettingsWithDependencies).FirstOrDefault(s => s.Kind != SettingKind.Secret && Logical(s.ParameterPath) == key);
                            var (group, description) = ParameterDocs.TryGetValue(key, out var doc) ? doc
                                : setting != null ? ("Aplicação", $"{(setting.Kind == SettingKind.Url ? "URL" : "E-mail")} {setting.Key} ({(setting.Source == SettingSource.Code ? "estava fixo no código" : "appSettings")}); revise o valor de cada ambiente")
                                : ("Aplicação", "");
                            parameters[key] = p = new InfraParameter { Name = key, Group = group, Description = description, Placeholder = setting == null && IsPlaceholder(key, value) };
                        }
                        p.Files.Add($"{t.ParametersFile}");
                        // The same parameter with a different value per template (MicroServiceName, capacities): say so instead of showing the first one.
                        if (!p.Values.TryGetValue(environment, out var existing)) p.Values[environment] = value;
                        else if (existing != value && existing != "(varia por arquivo)") p.Values[environment] = "(varia por arquivo)";
                    }
            guide.Parameters.AddRange(parameters.Values.OrderBy(p => p.Group switch { "Esteira" => 0, "Compartilhada" => 1, "Tags" => 2, "Dimensionamento" => 3, "Dados" => 4, _ => 5 }).ThenBy(p => p.Name, StringComparer.Ordinal));
        }
        else
        {
            guide.Files.Add(new InfraFile { Path = $"{InfraDir}/terraform/", Purpose = "módulo raiz Terraform (network, iam, ecs, alb, lambda, data, storage, cache, observability); terraform.tfvars.example traz o ponto de partida", DeployOrder = 1 });
            guide.Files.Add(new InfraFile { Path = $"{InfraDir}/README.md", Purpose = "ordem de execução (init/plan/apply por ambiente) e checklist de produção" });
            guide.Files.Add(new InfraFile { Path = ".github/workflows/deploy.yml", Purpose = "build, testes, imagens no ECR, update-service no ECS e deploy das Lambdas" });
            foreach (var (name, description, placeholder) in new[]
            {
                ("aws_region", "Região (sa-east-1).", false), ("environment", "Ambiente (dev/hom/prod): um workspace ou tfvars por ambiente.", false), ("app_name", "Nome da aplicação nos recursos.", false),
                ("vpc_cidr", "CIDR da VPC criada pelo módulo (ou troque o módulo network pela VPC da plataforma).", true), ("image_tag", "Tag das imagens no ECR publicadas pelo workflow.", false),
                ("certificate_arn", "Certificado ACM do ALB (HTTPS).", true), ("alert_email", "E-mail que recebe os alarmes (SNS).", true), ("app_settings", "Mapa com as URLs/e-mails externalizados (AppSettings__Secao__Chave → valor), um tfvars por ambiente.", false)
            })
            {
                var p = new InfraParameter { Name = name, Group = "Terraform", Description = description, Placeholder = placeholder };
                p.Files.Add("terraform.tfvars");
                guide.Parameters.Add(p);
            }
        }

        // ---------------------------------------------------------------- databases
        var dataAccess = result.AllDataAccess.ToList();
        foreach (var group in result.Databases.GroupBy(d => (d.Provider, d.Database ?? d.Name), StringTupleComparer.Instance))
        {
            var sample = group.First();
            var (provider, databaseName) = group.Key;
            var db = new DatabaseSetup
            {
                Name = databaseName, Provider = provider, SourceServer = sample.Server, IntegratedSecurity = group.Any(d => d.IntegratedSecurity),
                RdsEngine = plan.HasDb ? DefaultEngine(result) : null,
                Endpoint = cloudFormation ? $"export {plan.Feature}-<env>-DbEndpoint / DbPort da stack data (identificador {plan.Feature}-<env>)" : "output rds_endpoint do módulo data"
            };
            db.ConnectionNames.AddRange(group.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            db.UsedBy.AddRange(group.Select(d => d.Project).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            var tables = dataAccess.Where(t => t.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase) || db.ConnectionNames.Any(c => t.ConnectionNames.Contains(c))).DistinctBy(t => (t.Kind, t.QualifiedName)).ToList();
            db.Tables = tables.Count(t => t.Kind == DataObjectKind.Table);
            db.Procedures = tables.Count(t => t.Kind != DataObjectKind.Table);
            if (cloudFormation && plan.HasDb)
                foreach (var environment in Environments)
                    db.InstanceClass[environment] = DataParameters(result, environment, plan.Feature, plan.HasFsx).FirstOrDefault(p => p.Key == "DbInstanceClass").Value ?? "";
            foreach (var d in plan.Deployables.Where(d => db.UsedBy.Contains(d.Result.Project.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var secrets = SecretsFor(d).Where(s => db.ConnectionNames.Any(c => s.ConfigPath.Equals($"ConnectionStrings:{c}", StringComparison.OrdinalIgnoreCase) || s.ConfigPath.EndsWith($":{c}", StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var s in secrets) db.ConnectionSecrets.Add($"{s.SecretName} ({d.Result.Project.Name}, {(plan.Framework ? "gravado no config pelo CodeDeploy" : "variável " + s.EnvironmentVariable)})");
                if (secrets.Count == 0 && plan.Framework) db.ConnectionSecrets.Add($"{plan.App}/{d.Slug}/config → chave ConnectionStrings:{db.ConnectionNames[0]} ({d.Result.Project.Name}; JSON a preencher)");
            }
            db.Notes.Add("Senha master gerenciada pelo RDS (ManageMasterUserPassword; ARN em DbMasterSecretArn): crie o usuário da aplicação e grave a connection string nova no segredo acima.");
            db.Notes.Add(provider == "SQL Server" ? "Migração: backup .bak no S3 + rds_restore_database (ou AWS DMS para cutover com pouca parada). Licença inclusa no RDS; Express para bases pequenas." : "Migração: AWS DMS ou dump/restore nativo do engine.");
            if (db.IntegratedSecurity) db.Notes.Add("Integrated Security: exige AWS Managed Microsoft AD no RDS; prefira autenticação SQL com a senha no Secrets Manager.");
            if (!string.IsNullOrEmpty(sample.Server) && ModernizationAdvisor.IsInternalHost(sample.Server.Split(',')[0].Split('\\')[0])) db.Notes.Add($"Servidor de origem {sample.Server}: enquanto o banco não migrar, a aplicação na AWS precisa de rota (VPN/Direct Connect) e DNS para ele.");
            guide.Databases.Add(db);
        }

        // ---------------------------------------------------------------- secrets
        foreach (var (name, description) in plan.SecretNames)
        {
            var owners = plan.Deployables.Where(d => (plan.Framework && name == $"{plan.App}/{d.Slug}/config") || SecretsFor(d).Any(s => s.SecretName == name)).ToList();
            var extracted = owners.SelectMany(SecretsFor).FirstOrDefault(s => s.SecretName == name);
            var secret = new SecretSetup
            {
                Name = name, Holds = description,
                DeliveredAs = extracted == null ? "chaves do config da instância (after-install.ps1)" : plan.Framework ? $"chave {extracted.ConfigPath} do config da instância (after-install.ps1)" : owners.Any(o => o.Result.Hosting!.Primary == AwsHosting.Lambda) ? $"variável {extracted.EnvironmentVariable}_SECRET com o nome; leia com AWSSDK.SecretsManager" : $"variável de ambiente {extracted.EnvironmentVariable} (bloco Secrets da task definition)",
                HowToFill = extracted == null ? "manual: JSON chave→valor com as connection strings e demais chaves do web.config/app.config que não podem ficar no repositório" : $"_secrets/{owners.First().Result.Project.Name}/create-secrets.sh (o valor que estava no config/código fica em _secrets/, fora do git); rotacione a credencial depois",
                CreatedBy = cloudFormation ? $"{InfraDir}/data.yml (SecretString: PREENCHER)" : "_secrets/<projeto>/create-secrets.sh (Terraform só referencia por nome)"
            };
            secret.UsedBy.AddRange(owners.Select(o => o.Result.Project.Name));
            guide.Secrets.Add(secret);
        }
        if (plan.HasDb)
            guide.Secrets.Add(new SecretSetup { Name = $"rds!db-... (gerenciado; ARN em DbMasterSecretArn)", Holds = "senha master do RDS", DeliveredAs = "não vai para a aplicação", HowToFill = "automático (RDS); use só para criar o usuário da aplicação", CreatedBy = cloudFormation ? $"{InfraDir}/data.yml (ManageMasterUserPassword)" : "módulo data" });

        // ---------------------------------------------------------------- settings
        foreach (var (setting, parameter, usedBy) in SettingsFor(plan.Deployables))
        {
            var s = new SettingSetup
            {
                Key = setting.Key, Kind = setting.Kind == SettingKind.Url ? "URL" : "E-mail", Parameter = cloudFormation ? parameter : $"app_settings[\"{setting.EnvironmentVariable}\"]",
                EnvironmentVariable = plan.Framework ? null : setting.EnvironmentVariable,
                ParameterStorePath = plan.Framework ? $"/{plan.Feature}/<env>/{setting.ParameterPath}" : null,
                Source = setting.Source == SettingSource.Code ? $"fixo no código ({setting.Location})" : $"appSettings ({setting.Location})"
            };
            foreach (var environment in Environments) s.Values[environment] = SettingValue(setting, environment);
            s.UsedBy.AddRange(usedBy.Select(d => d.Result.Project.Name));
            guide.Settings.Add(s);
        }
        if (!plan.Framework)
        {
            foreach (var d in plan.Services)
            {
                var isWeb = d.Result.Project.Kind == ProjectKind.Web;
                Fixed($"{(isWeb ? "ASPNETCORE_ENVIRONMENT" : "DOTNET_ENVIRONMENT")}", "Production", "ambiente .NET (appsettings.Production.json)", d);
                Fixed("ambiente", "<env>", "ambiente da plataforma (dev/hom/prod)", d);
                if (plan.HasS3) Fixed("FILES_BUCKET", $"{plan.Feature}-<env>-<conta>-files", "bucket dos arquivos (export FilesBucketName)", d);
                if (plan.Workers.Contains(d)) Fixed("QUEUE_URL", $"https://sqs.<região>.amazonaws.com/<conta>/{plan.Feature}-<env>-{d.Slug}", "fila consumida pelo worker (export QueueUrl)", d);
            }
            foreach (var l in plan.Lambdas)
            {
                Fixed("DOTNET_ENVIRONMENT", "Production", "ambiente .NET", l);
                Fixed("ENVIRONMENT", "<env>", "ambiente da plataforma", l);
                if (plan.HasS3) Fixed("FILES_BUCKET", $"{plan.Feature}-<env>-<conta>-files", "bucket dos arquivos", l);
            }
        }
        void Fixed(string variable, string value, string purpose, Deployable d)
        {
            var existing = guide.Settings.FirstOrDefault(s => s.Kind == "Ambiente" && s.EnvironmentVariable == variable && s.Values.GetValueOrDefault(Environments[0]) == value.Replace("<env>", Environments[0]));
            if (existing != null) { if (!existing.UsedBy.Contains(d.Result.Project.Name)) existing.UsedBy.Add(d.Result.Project.Name); return; }
            var s = new SettingSetup { Key = variable, Kind = "Ambiente", Parameter = "fixo no template", EnvironmentVariable = variable, Source = purpose };
            foreach (var environment in Environments) s.Values[environment] = value.Replace("<env>", environment);
            s.UsedBy.Add(d.Result.Project.Name);
            guide.Settings.Add(s);
        }

        // ---------------------------------------------------------------- storage, queues
        string Replaces(string id) => arch.Components.FirstOrDefault(c => c.Id == id)?.Replaces ?? "";
        List<string> Users(string id) => arch.Components.FirstOrDefault(c => c.Id == id)?.UsedBy.ToList() ?? [];
        if (plan.HasS3)
        {
            var bucket = new ResourceSetup { Service = "Amazon S3", Name = $"{plan.Feature}-<env>-<conta>-files", Purpose = "arquivos da aplicação (entrada/saída, uploads, exportações)", Replaces = Replaces("s3"), DeliveredAs = plan.Framework ? "compartilhamento SMB via Storage Gateway (File Gateway) ou caminho \\\\<gateway>\\<bucket>" : "variável FILES_BUCKET / export FilesBucketName" };
            bucket.UsedBy.AddRange(Users("s3"));
            if (plan.FileAutomations.Count > 0) bucket.Notes.Add($"Notificações de objetos criados vão para a fila {plan.Feature}-<env>-files-events (DLQ -dlq), gatilho das Lambdas {string.Join(", ", plan.FileAutomations.Select(f => f.Result.Project.Name))}.");
            if (plan.Framework) bucket.Notes.Add("Com File Gateway o código continua lendo pastas; configure o gateway (appliance ou EC2) e monte o compartilhamento nas instâncias.");
            guide.Storage.Add(bucket);
        }
        if (plan.HasFsx)
        {
            var fsx = new ResourceSetup { Service = "Amazon FSx for Windows File Server", Name = $"\\\\<FsxDnsName>\\share (export {plan.Feature}-<env>-FsxDnsName)", Purpose = "pastas de rede (UNC) montadas nas instâncias com os mesmos nomes", Replaces = Replaces("fsx"), DeliveredAs = "DNS do file system exportado pela stack data; mapeie no user data/CodeDeploy" };
            fsx.UsedBy.AddRange(Users("fsx"));
            fsx.Notes.Add("Exige ActiveDirectoryId (AWS Managed Microsoft AD) em parameters-data.json; copie os dados com robocopy ou AWS DataSync.");
            guide.Storage.Add(fsx);
        }
        if (plan.Framework)
        {
            var artifacts = new ResourceSetup { Service = "Amazon S3", Name = $"{plan.Feature}-<env>-<conta>-deploy", Purpose = "artefatos (zip) do CodeDeploy", DeliveredAs = "export ArtifactsBucketName; usado pelo workflow/esteira" };
            artifacts.UsedBy.AddRange(plan.Deployables.Select(d => d.Result.Project.Name));
            guide.Storage.Add(artifacts);
        }
        if (plan.Lambdas.Count > 0)
        {
            var packages = new ResourceSetup { Service = "Amazon S3", Name = $"{plan.Feature}-<env>-artifacts (PackageBucket)", Purpose = "pacotes .zip das Lambdas (dotnet lambda package)", DeliveredAs = "parâmetros PackageBucket/PackageKey" };
            packages.UsedBy.AddRange(plan.Lambdas.Select(l => l.Result.Project.Name));
            packages.Notes.Add("Crie o bucket (ou aponte para o da esteira) antes do primeiro deploy da stack da função.");
            guide.Storage.Add(packages);
        }
        foreach (var w in plan.Workers)
        {
            var queue = new ResourceSetup { Service = "Amazon SQS", Name = $"{plan.Feature}-<env>-{w.Slug} (+ -dlq)", Purpose = $"fila consumida por {w.Result.Project.Name}", Replaces = Replaces("sqs"), DeliveredAs = "variável QUEUE_URL / export QueueUrl" };
            queue.UsedBy.Add(w.Result.Project.Name);
            queue.Notes.Add("Produtores precisam publicar nesta fila (SDK AWS) no lugar da fila antiga.");
            guide.Queues.Add(queue);
        }
        if (plan.FileAutomations.Count > 0)
        {
            var events = new ResourceSetup { Service = "Amazon SQS", Name = $"{plan.Feature}-<env>-files-events (+ -dlq)", Purpose = "eventos de objetos criados no bucket de arquivos (S3 → SQS → Lambda)", DeliveredAs = "event source mapping da Lambda" };
            events.UsedBy.AddRange(plan.FileAutomations.Select(f => f.Result.Project.Name));
            guide.Queues.Add(events);
        }
        if (!plan.Framework && plan.Workers.Count == 0 && arch.Components.Any(c => c.Id is "sqs" or "mq"))
            foreach (var c in arch.Components.Where(c => c.Id is "sqs" or "mq"))
            {
                var queue = new ResourceSetup { Service = c.Service, Name = "a definir (não gerado: nenhum worker ECS identificado)", Purpose = c.Role, Replaces = c.Replaces };
                queue.UsedBy.AddRange(c.UsedBy);
                guide.Queues.Add(queue);
            }

        // ---------------------------------------------------------------- integrations
        foreach (var host in result.InternalHosts)
        {
            var i = new IntegrationSetup { Kind = "Rede on-premises", Target = host, Action = "rota pela VPN/Direct Connect, regra de firewall de saída da VPC e resolução de nome (Route 53 Resolver outbound) para o host interno" };
            i.UsedBy.AddRange(profiles.Where(p => p.Profile.ExternalEndpoints.Any(e => e.Contains(host, StringComparison.OrdinalIgnoreCase)) || p.Profile.Databases.Any(d => (d.Server ?? "").Contains(host, StringComparison.OrdinalIgnoreCase))).Select(p => p.Result.Project.Name).Distinct());
            guide.Integrations.Add(i);
        }
        var handled = new HashSet<string>(StringComparer.Ordinal) { "rds", "rds-sqlserver", "rds-oracle", "rds-mysql", "rds-postgres", "rds-other", "s3", "fsx", "sqs", "secrets", "ssm", "cloudwatch", "ecr", "cicd", "ec2", "alb", "nlb", "vpc", "route53", "ecs", "lambda", "s3-events", "scheduler", "backup", "vpn" };
        foreach (var c in arch.Components.Where(c => !handled.Contains(c.Id)))
        {
            var i = new IntegrationSetup
            {
                Kind = c.Service, Target = c.Replaces.Length > 0 ? c.Replaces : c.Role,
                Action = c.Id switch
                {
                    "ses" => "verificar o domínio remetente, sair do sandbox, criar credenciais SMTP (Secrets Manager) e apontar o host SMTP da aplicação para email-smtp.<região>.amazonaws.com:587",
                    "ses-inbound" => "regra de recebimento do SES (MX do domínio → SES → S3) para os endereços em InboundRecipients",
                    "graph" => "registrar aplicação no Entra ID com permissão Mail.Read na caixa postal e guardar client id/secret no Secrets Manager (EWS será bloqueado)",
                    "ad" => "AWS Managed Microsoft AD (ou trust com o AD on-premises) para Integrated Security/FSx/autenticação Windows",
                    "elasticache" => "cluster Redis e connection string no Secrets Manager; sessão/cache saem da memória do processo",
                    "cognito" => "user pool/identity provider e ajuste da autenticação da aplicação",
                    "cloudfront" => "distribuição na frente do ALB para estáticos/TLS; registrar DNS",
                    "transfer" => "servidor SFTP (AWS Transfer Family) sobre o bucket para parceiros que enviam arquivos",
                    "storage-gateway" => "File Gateway (appliance/EC2) expondo o bucket como SMB; montar nas instâncias",
                    "mq" => "broker Amazon MQ e connection string no Secrets Manager",
                    _ => c.Why
                }
            };
            i.UsedBy.AddRange(c.UsedBy);
            guide.Integrations.Add(i);
        }

        // ---------------------------------------------------------------- pipeline
        foreach (var environment in Environments) guide.Pipeline.Add(new PipelineSetup { File = ".iupipes.yml", Key = $"deploy.aws.{environment}.account", Value = "123456789012", Action = $"conta AWS de {environment}" });
        guide.Pipeline.Add(new PipelineSetup { File = ".iupipes.yml", Key = "project.language", Value = plan.Framework ? "dotnet-framework" : "dotnet-core", Action = plan.Framework ? "confirme com a plataforma o valor para .NET Framework (build com MSBuild em agente Windows)" : "build .NET 10 com imagem linux/arm64" });
        guide.Pipeline.Add(new PipelineSetup { File = ".iupipes.yml", Key = "infra.cloudformation.aws-owner-contact-email / aws-tech-team-email", Value = "po@empresa.com.br / time@empresa.com.br", Action = "e-mails do PO e do time técnico" });
        guide.Pipeline.Add(new PipelineSetup { File = ".iupipes.yml", Key = "security.fortify.sigla / sigla-app", Value = "SIGLA / SIGLA-APP", Action = "sigla da aplicação no catálogo" });
        guide.Pipeline.Add(new PipelineSetup { File = ".iupipes.yml", Key = "quality.sonar.gate-name", Value = "''", Action = "quality gate do Sonar da squad" });
        if (cloudFormation) guide.Pipeline.Add(new PipelineSetup { File = $"{InfraDir}/<env>/parameters*.json", Key = "DevToolsAccount", Value = "123456789012", Action = "conta das ferramentas da esteira (ECR/artefatos)" });
        guide.Pipeline.Add(new PipelineSetup { File = "tests/testspec-dev.yml, testspec-hom.yml", Key = "phases.build.commands", Value = "echo 'Testes executados manualmente'", Action = plan.Framework ? "automatize: smoke test da URL publicada e testes via vstest" : "automatize: dotnet test app/src e smoke test de /health" });
        guide.Pipeline.Add(new PipelineSetup { File = ".github/workflows/deploy.yml", Key = "secrets.AWS_DEPLOY_ROLE_ARN / vars", Value = "a definir", Action = "só se a esteira da plataforma não for usada: role OIDC do GitHub e conta por ambiente" });

        // ---------------------------------------------------------------- checklist
        void Step(string phase, string step, string? detail = null) => guide.Checklist.Add(new ChecklistStep { Phase = phase, Step = step, Detail = detail });
        Step("1. Preparar", "Preencher os placeholders dos parâmetros de cada ambiente", cloudFormation ? $"{InfraDir}/dev|hom|prod/parameters*.json: VPC, subnets, roles, listener/cluster, conta da esteira, tags; revisar as URLs/e-mails por ambiente" : "terraform.tfvars por ambiente: VPC, certificado, e-mail de alertas, app_settings");
        Step("1. Preparar", "Preencher .iupipes.yml e abrir o repositório na esteira", "contas por ambiente, sigla, e-mails, language; subir app/src, infra/, tests/ e .iupipes.yml (nunca _secrets/)");
        if (plan.NotGenerated.Count > 0) Step("1. Preparar", $"Converter os projetos sem infra gerada ({string.Join(", ", plan.NotGenerated.Select(p => p.Project.Name))})", "VB.NET: Upgrade Assistant ou CodeConverter, depois rodar o Migrator de novo");
        if (plan.HasData || !cloudFormation) Step("2. Dados e segredos", cloudFormation ? $"Criar a stack de dados: ./deploy.sh dev → {plan.Feature}-dev-data" : "terraform apply do módulo data", string.Join(", ", new[] { plan.HasDb ? "RDS" : null, plan.HasS3 ? "bucket" : null, plan.HasFsx ? "FSx" : null, plan.Workers.Count > 0 ? "filas" : null, "nomes dos segredos" }.Where(x => x != null)));
        if (plan.SecretNames.Count > 0) Step("2. Dados e segredos", "Colocar os valores nos segredos", $"_secrets/<projeto>/create-secrets.sh para os extraídos{(plan.Framework ? $"; JSON manual em {plan.App}/<projeto>/config" : "")}; nada fica em PREENCHER");
        if (plan.HasDb) Step("2. Dados e segredos", "Migrar o banco e gravar a connection string nova no segredo", "restore .bak/DMS, usuário da aplicação, Encrypt/TrustServerCertificate conforme o engine");
        if (plan.HasFsx) Step("2. Dados e segredos", "Copiar as pastas de rede para o FSx", "robocopy/DataSync; mesmos nomes de pasta");
        if (plan.HasS3 && plan.Framework) Step("2. Dados e segredos", "Configurar o File Gateway sobre o bucket e montar nas instâncias");
        Step("3. Serviços", plan.Framework ? "Criar as stacks de serviço (./deploy.sh <env>) e publicar pela esteira" : "Publicar as imagens no ECR e criar as stacks de serviço (./deploy.sh <env>)", plan.Framework ? "user data instala IIS/.NET 4.8.1/agentes; CodeDeploy publica o zip com codedeploy/<micro>/" : "buildx linux/arm64; ImageTag nos parâmetros; uma porta por serviço no NLB");
        if (plan.Lambdas.Count > 0) Step("3. Serviços", "Empacotar e publicar as Lambdas", "dotnet lambda package → PackageBucket/PackageKey → stack lambda-<micro>.yml");
        foreach (var i in guide.Integrations.Where(i => i.Kind != "Rede on-premises")) Step("3. Serviços", $"{i.Kind}: {i.Action}");
        if (result.InternalHosts.Count > 0) Step("3. Serviços", "Liberar a rede para os hosts on-premises ainda usados", string.Join(", ", result.InternalHosts));
        Step("4. Validar", plan.Framework ? "Health check do target group em 200 e validate-service.ps1 passando" : "/health em 200 no NLB e task estável (circuit breaker)", "ajustar HealthCheckPath/porta se preciso; conferir logs no CloudWatch/Datadog");
        Step("4. Validar", "Rodar os testes de aceitação (TAAC) em dev e hom", "tests/testspec-*.yml; automatizar o echo");
        Step("4. Validar", "Testar o comportamento, não só a subida", "autenticação, sessão, uploads, agendamentos, envio de e-mail, integrações");
        Step("5. Cutover", "Apontar o DNS para o ALB/NLB compartilhado", plan.Framework ? "registro Route 53 para ListenerRuleHost" : "registro para o NLB da feature");
        Step("5. Cutover", "Rotacionar as credenciais que estavam em texto claro no repositório antigo");
        Step("6. Produção", plan.Framework ? "Capacidade e imagem: DesiredCapacity ≥ 2 nas web, AMI do EC2 Image Builder, ingress só do ALB" : "Capacidade: CapacityProvider FARGATE e DesiredNumberOfTasks ≥ 2 nas web, ingress só do NLB");
        Step("6. Produção", "Alarmes: ajustar o FilterPattern ao formato de log e o tópico SNS", SnsTopic);
        Step("6. Produção", "Backups e retenção", "AWS Backup/retenção do RDS, lifecycle dos buckets, retenção dos log groups");
        return guide;
    }

    /// <summary>MicroServiceName for a project that has no Deployable (not generated): same rule as <see cref="Micro"/>.</summary>
    private static string MicroOf(SolutionResult result, string projectName)
    {
        var name = projectName.StartsWith(result.SolutionName + ".", StringComparison.OrdinalIgnoreCase) ? projectName[(result.SolutionName.Length + 1)..] : projectName;
        var micro = Letters(name);
        return micro.Length == 0 || micro == Feature(result) ? Letters(projectName) : micro;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string, string) x, (string, string) y) => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) obj) => HashCode.Combine(obj.Item1.ToLowerInvariant(), obj.Item2.ToLowerInvariant());
    }
}
