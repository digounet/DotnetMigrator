using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// <c>--iac terraform-platform</c>: Terraform in the shape the platform's pipeline expects, calling the platform's own modules instead of
/// creating VPC/ALB/IAM from scratch. Reproduces the layout of the portfolio's migrated repositories:
/// <c>infra/terraform/{main,locals,variables,iamsr,sg,data,outputs,provider}.tf</c> (worker module + IAM service roles module),
/// <c>iamsr/{policy,trust}-template/*.json</c>, <c>inventories/{dev,hom,prod}/terraform.tfvars</c>, a Dockerfile for the prebuilt publish
/// (Alpine ARM64, corporate CA bundle, Datadog APM), <c>.iupipes.yml</c> with <c>infra.terraform</c> and the TAAC specs.
/// Only ECS workers are generated (the platform's worker module); web, Lambda and Windows hosting are listed as not generated.
/// Secrets stay in ONE Secrets Manager secret per application (JSON with one key per credential), injected through <c>task_secrets</c>
/// (<c>${arn}:Key::</c>); the values go to <c>_secrets/</c>, never to Terraform.
/// </summary>
public static partial class PlatformTerraformGenerator
{
    public const string WorkerModuleSource = "git::https://github.com/itau-corp/itau-hn8-modules-ecs-service.git//modules/worker?ref=v0.55.0";
    public const string IamsrModuleSource = "git::https://github.com/itau-corp/itau-ey4-modulo-iamsr.git?ref=v2.2.1";
    /// <summary>Central ECR accounts the platform pulls base/sidecar images from (they appear in the execution policy of every service).</summary>
    private static readonly string[] PlatformEcrAccounts = ["851725494844", "891377148421"];
    public const int ContainerPort = 8080;
    private const string PlaceholderAccount = "000000000000";
    private static readonly string[] Environments = ["dev", "hom", "prod"];

    private sealed record Worker(ProjectResult Result, ApplicationProfile Profile, string Id, string Micro);

    /// <summary>Projects the worker module covers: ECS Fargate workers with an output project.</summary>
    private static List<Worker> Workers(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles) =>
        profiles.Where(p => p.Result.Hosting is { Primary: AwsHosting.EcsFargateWorker } && p.Result.OutputProjectPath != null)
            .Select(p => new Worker(p.Result, p.Profile, Id(p.Result.Project.Name), Micro(result, p.Result.Project.Name)))
            .ToList();

    /// <summary>Deployables the platform modules cannot express yet (web service, Lambda, Windows, scheduled task): the user takes them from the platform's templates.</summary>
    public static List<ProjectResult> NotGenerated(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles) =>
        profiles.Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop or AwsHosting.EcsFargateWorker) } && p.Result.OutputProjectPath != null).Select(p => p.Result).ToList();

    public static string AppSecretName(SolutionResult result) => CloudFormationGenerator.Logical(result.SolutionName) is { Length: > 0 } n ? n : "App";

    public static Dictionary<string, string> Generate(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var workers = Workers(result, profiles);
        if (workers.Count == 0) return files;
        var feature = CloudFormationGenerator.Feature(result);
        var secretName = AppSecretName(result);
        var many = workers.Count > 1;

        files["infra/terraform/provider.tf"] = Provider();
        files["infra/terraform/data.tf"] = Data();
        files["infra/terraform/variables.tf"] = Variables();
        files["infra/terraform/locals.tf"] = Locals(result, feature);
        files["infra/terraform/iamsr.tf"] = IamsrTemplates();
        foreach (var (path, content) in IamsrDocuments(secretName)) files[$"infra/terraform/{path}"] = content;
        foreach (var w in workers)
        {
            var suffix = many ? "-" + w.Id.Replace('_', '-') : "";
            files[$"infra/terraform/main{suffix}.tf"] = Main(w, feature, many);
            files[$"infra/terraform/iamsr{suffix}-module.tf"] = IamsrModule(w, many);
            files[$"infra/terraform/sg{suffix}.tf"] = SecurityGroup(w, many);
            files[$"infra/terraform/outputs{suffix}.tf"] = Outputs(w, many);
        }
        foreach (var environment in Environments) files[$"infra/terraform/inventories/{environment}/terraform.tfvars"] = TfVars(result, workers, environment, secretName);

        var secrets = SecretsOf(workers);
        foreach (var environment in Environments)
            if (secrets.Any(s => AppliesTo(s, environment)))
                files[$"{SecretsExtractor.RootFolder}/platform/secret-{environment}.json"] = SecretJson(secrets, environment);
        if (secrets.Count > 0)
        {
            files[$"{SecretsExtractor.RootFolder}/platform/create-platform-secret.sh"] = CreateSecretScript(secretName, secrets);
            files[$"{SecretsExtractor.RootFolder}/platform/README.md"] = SecretsReadme(secretName, secrets);
        }

        for (var i = 0; i < workers.Count; i++)
            files[i == 0 ? "Dockerfile" : $"Dockerfile.{workers[i].Micro}"] = Dockerfile(workers[i]);
        files[".iupipes.yml"] = Iupipes(result, workers, feature);
        foreach (var environment in new[] { "dev", "hom" }) files[$"tests/testspec-{environment}.yml"] = TestSpec();
        files["infra/README.md"] = Readme(result, workers, NotGenerated(result, profiles), secretName, secrets);
        return files;
    }

    // ------------------------------------------------------------------ secrets

    /// <summary>One entry per credential the workers need (own + referenced libraries), keyed by environment variable; the Terraform only sees the keys.</summary>
    private static List<ExtractedSecret> SecretsOf(List<Worker> workers) =>
        workers.SelectMany(w => w.Result.SecretsWithDependencies).DistinctBy(s => (s.EnvironmentVariable, s.Environment)).ToList();

    /// <summary>Config files per environment (Development, Staging, Production); a secret without environment (from code) applies to all.</summary>
    private static bool AppliesTo(ExtractedSecret secret, string environment) => secret.Environment == null || secret.Environment switch
    {
        "Development" => environment == "dev",
        "Staging" => environment == "hom",
        "Production" => environment == "prod",
        _ => false
    };

    private static string SecretJson(List<ExtractedSecret> secrets, string environment)
    {
        var root = new JsonObject();
        // An environment-specific value wins over one that came from code (Environment == null).
        foreach (var s in secrets.Where(s => AppliesTo(s, environment)).OrderBy(s => s.Environment == null ? 0 : 1)) root[s.EnvironmentVariable] = s.Value;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static string CreateSecretScript(string secretName, List<ExtractedSecret> secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine($"# Cria/atualiza o segredo único da aplicação ({secretName}) no Secrets Manager da conta do ambiente, a partir de secret-<ambiente>.json.");
        sb.AppendLine("# Uso: AWS_REGION=sa-east-1 ./create-platform-secret.sh dev|hom|prod   (AWS CLI autenticada na conta do ambiente)");
        sb.AppendLine("# Depois copie o ARN completo do resultado para application_secret_arn em infra/terraform/inventories/<ambiente>/terraform.tfvars.");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine("cd \"$(dirname \"$0\")\"");
        sb.AppendLine("ENV=\"${1:?informe dev, hom ou prod}\"");
        sb.AppendLine("REGION=\"${AWS_REGION:-sa-east-1}\"");
        sb.AppendLine("FILE=\"secret-${ENV}.json\"");
        sb.AppendLine("[ -f \"$FILE\" ] || { echo \"sem $FILE: nenhuma credencial para este ambiente\"; exit 1; }");
        sb.AppendLine($"NAME=\"{secretName}\"");
        sb.AppendLine("if aws secretsmanager describe-secret --secret-id \"$NAME\" --region \"$REGION\" >/dev/null 2>&1; then");
        sb.AppendLine("  aws secretsmanager put-secret-value --secret-id \"$NAME\" --secret-string \"file://$FILE\" --region \"$REGION\" --query ARN --output text");
        sb.AppendLine("else");
        sb.AppendLine("  aws secretsmanager create-secret --name \"$NAME\" --secret-string \"file://$FILE\" --region \"$REGION\" --query ARN --output text");
        sb.AppendLine("fi");
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string SecretsReadme(string secretName, List<ExtractedSecret> secrets) => $"""
        # Segredo da aplicação ({secretName})

        A plataforma entrega as credenciais ao container por **um segredo JSON no Secrets Manager** (uma chave por credencial) referenciado em
        `task_secrets` como `<arn>:<Chave>::`. O Terraform só conhece o ARN e os nomes das chaves (`secret_keys` em cada `terraform.tfvars`).

        | Arquivo | Uso |
        |---|---|
        | `secret-dev.json`, `secret-hom.json`, `secret-prod.json` | valores retirados do código e dos JSON de configuração, por ambiente |
        | `create-platform-secret.sh` | cria/atualiza o segredo `{secretName}` na conta do ambiente e imprime o ARN |

        Chaves:
        {string.Join("\n", secrets.Select(s => $"- `{s.EnvironmentVariable}` ← `{s.ConfigPath}`{(s.Environment != null ? $" ({s.Environment})" : " (código)")}"))}

        Esta pasta NÃO vai para o repositório. Os valores antigos estavam em texto claro na aplicação original: rotacione-os.
        """.Replace("\r\n", "\n");

    // ------------------------------------------------------------------ terraform files

    private static string Provider() => """
        terraform {
          required_providers {
            aws = {
              source  = "hashicorp/aws"
              version = ">= 4.40.0, != 5.59.0, != 5.60.0, <= 5.64.0"
            }
          }

          required_version = ">= 1.5.7"
        }
        """.Replace("\r\n", "\n") + "\n";

    private static string Data() => """
        data "aws_caller_identity" "current" {}
        data "aws_region" "current" {}
        data "aws_partition" "current" {}

        data "aws_ecs_cluster" "current" {
          cluster_name = var.ecs_cluster_name
        }

        data "aws_vpc" "task" {
          id = var.service_vpc_id
        }
        """.Replace("\r\n", "\n") + "\n";

    private static string Variables() => """
        ############## definições basicas do service
        variable "environment" {
          type = string
        }

        variable "dotnet_environment" {
          type        = string
          default     = "Production"
          description = "Valor de ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT (Development, Staging, Production)."
        }

        variable "ecs_cluster_name" {
          type        = string
          description = "Nome do cluster ECS Fargate compartilhado onde o worker será executado."
        }

        variable "service_desired_count" {
          type        = number
          default     = 1
          description = "Número desejado de tasks do worker (um ciclo não deve rodar em duas tasks ao mesmo tempo)."
          validation {
            condition     = var.service_desired_count >= 1
            error_message = "service_desired_count deve ser >= 1."
          }
        }

        variable "service_deployment_maximum_percent" {
          type    = number
          default = 200
        }

        variable "service_launch_config" {
          type = object({
            launch_type = string
            capacity_providers = optional(list(object({
              capacity_provider = string
              base              = number
              weight            = number
              upper_case        = optional(bool, true)
            })), [])
          })
          validation {
            condition     = contains(["EC2", "FARGATE"], upper(var.service_launch_config.launch_type))
            error_message = "A variavel 'service_launch_config.launch_type' deve ser 'EC2' ou 'FARGATE'."
          }
        }

        variable "service_min_capacity" {
          type        = number
          description = "Capacidade mínima para auto scaling."
        }

        variable "service_max_capacity" {
          type        = number
          description = "Capacidade máxima para auto scaling."
        }

        variable "task_environment_vars" {
          description = "Variáveis de ambiente extras para as tasks (ex: [{ name = 'ENV_VAR_NAME', value = 'value' }])."
          type = list(object({
            name  = string
            value = string
          }))
          default = []
        }

        ##############  configurações mínimas de rede
        variable "service_vpc_id" {
          type        = string
          description = "ID da VPC onde o worker será executado."
          validation {
            condition     = can(regex("^vpc-[0-9a-zA-Z-]+$", var.service_vpc_id))
            error_message = "service_vpc_id deve começar com 'vpc-'."
          }
        }

        variable "service_cidr_blocks" {
          type    = list(string)
          default = ["10.0.0.0/8"]
          validation {
            condition     = alltrue([for cidr in var.service_cidr_blocks : can(cidrnetmask(cidr))])
            error_message = "Erro ao configurar 'service_cidr_blocks': informe CIDRs válidos (ex. 84.240.40.0/24)."
          }
        }

        variable "service_subnets" {
          type        = list(string)
          description = "Subnets do worker (múltiplas AZs recomendado)."
          validation {
            condition     = length(var.service_subnets) >= 1
            error_message = "service_subnets deve ter pelo menos 1 subnet."
          }
        }

        ############## definições basicas do container
        variable "task_cpu" {
          type    = number
          default = 512
        }

        variable "task_memory" {
          type    = number
          default = 1024
        }

        variable "task_ulimits_vars" {
          type = list(object({
            name      = string
            softLimit = number
            hardLimit = number
          }))
          default = []
        }

        variable "task_log_driver" {
          type = object({
            log-driver                        = string
            cloudwatch-retention-logs-in-days = optional(number, 7)
            splunk-format                     = optional(string, "raw")
            splunk-index                      = optional(string, "")
            splunk-token                      = optional(string, "")
            tag                               = optional(string, " ")
            options                           = optional(map(any), {})
            secret-options                    = optional(map(any), {})
          })
          validation {
            condition     = contains(["awslogs", "splunk", "awsfirelens"], lower(var.task_log_driver.log-driver))
            error_message = "A variavel 'task_log_driver.log-driver' deve ser awslogs, splunk ou awsfirelens."
          }
          default = {
            log-driver = "awslogs"
            options = {
              awslogs-create-group      = "false"
              awslogs-retention-in-days = 1
            }
          }
        }

        variable "task_ephemeral_storage_size_in_gib" {
          type    = number
          default = 0
        }

        ############## tags obrigatorias (preenchidas pela esteira)
        variable "github_repo_name" {
          type    = string
          default = "GITHUB_REPOSITORY_TAG_PLACEHOLDER"
        }

        variable "github_repo_id" {
          type    = string
          default = "GITHUB_REPOSITORY_TAG_PLACEHOLDER"
        }

        variable "iupipes_build_version" {
          type    = string
          default = "GITHUB_REPOSITORY_TAG_PLACEHOLDER"
        }

        ############## Custom
        variable "application_secret_arn" {
          type        = string
          description = "ARN completo do segredo JSON da aplicação (nunca o valor). Criado por _secrets/platform/create-platform-secret.sh."
          validation {
            condition     = can(regex("^arn:[^:]+:secretsmanager:[^:]+:[0-9]{12}:secret:.+$", var.application_secret_arn))
            error_message = "Informar o ARN completo do secret existente, nunca seu valor."
          }
        }

        variable "secret_keys" {
          type        = map(string)
          default     = {}
          description = "Variável de ambiente da aplicação → chave dentro do segredo JSON (um por credencial)."
        }

        variable "app_settings" {
          type        = map(string)
          default     = {}
          description = "URLs, e-mails e pastas que estavam fixos no código/JSON: variável de ambiente (Secao__Chave) → valor do ambiente."
        }

        variable "app_environment" {
          type        = map(string)
          default     = {}
          description = "Variáveis de ambiente que o código lê diretamente (Environment.GetEnvironmentVariable) e que o runtime não fornece."
        }

        variable "secret_kms_key_arn" {
          type    = string
          default = ""
        }
        """.Replace("\r\n", "\n") + "\n";

    private static string Locals(SolutionResult result, string feature) => $$"""
        locals {
          sigla               = "PREENCHER"
          sigla_app           = "PREENCHER-APP"
          produto             = "PREENCHER"
          context             = "PREENCHER"
          owner_contact_email = "po@empresa.com.br"
          tech_team_email     = "time@empresa.com.br"
          finalidade          = "modernizacao"
          squad               = "PREENCHER"
          feature_name        = "{{feature}}"
          servico_negocio     = "N/A"
          centro_custo        = "N/A"

          additional_tags = {
            "REPO_ID"                            = var.github_repo_id
            "iu:finops:alocacao:canal"           = "GLOBAL"
            "iu:finops:alocacao:centro-custo"    = local.centro_custo
            "iu:finops:alocacao:empresa"         = "333"
            "iu:finops:alocacao:sigla"           = local.sigla
            "iu:finops:alocacao:produto"         = local.produto
            "iu:finops:alocacao:projeto"         = "N/A"
            "iu:finops:alocacao:servico-negocio" = local.servico_negocio
            "iu:finops:alocacao:squad"           = local.squad
            "iu:finops:alocacao:usuario"         = local.owner_contact_email
            "iu:finops:alocacao:dados"           = "N/A"
          }

          task_container_port = [
            {
              containerPort = {{ContainerPort}}
            }
          ]

          default_container_port = local.task_container_port[0].containerPort

          health_check_url = "http://localhost:${local.default_container_port}/health"

          task_health_check = {
            command     = ["CMD-SHELL", "curl -f ${local.health_check_url} || exit 1"]
            interval    = 60
            timeout     = 5
            retries     = 3
            startPeriod = 30
          }

          # URLs/e-mails/pastas (app_settings), variáveis lidas pelo código (app_environment) e o ambiente .NET.
          task_environment_vars = concat(
            [
              { name = "ASPNETCORE_ENVIRONMENT", value = var.dotnet_environment },
              { name = "DOTNET_ENVIRONMENT", value = var.dotnet_environment },
            ],
            [for name, value in var.app_environment : { name = name, value = value }],
            [for name, value in var.app_settings : { name = name, value = value }],
          )

          # Um item por credencial: ${arn}:Chave:: (formato do ECS para segredo JSON).
          task_secrets = [for name, key in var.secret_keys : {
            name      = name
            valueFrom = "${var.application_secret_arn}:${key}::"
          }]

          task_runtime_platform = {
            cpuArchitecture       = "ARM64"
            operatingSystemFamily = "LINUX"
          }

          task_datadog = {
            apm = {
              enabled          = true
              ignore_resources = ["GET /metrics", "GET /health"]
              log_level        = "INFO"
            }
            log = {
              enabled = true
              source  = "csharp"
            }
            health_check = {
              enabled     = true
              url         = local.health_check_url
              tags        = ["path:/health"]
              interval    = 30
              timeout     = 5
              retries     = 3
              startPeriod = 260
            }
            additional_tags = {
              "sigla_app" = local.sigla_app
            }
          }

          # Auto scaling: CPU 80%/60%, memória 90%/60% (padrão da plataforma).
          autoscaling = {
            enabled      = var.service_desired_count > 0
            min_capacity = var.service_min_capacity
            max_capacity = var.service_max_capacity
            cpu = {
              enabled = true
              scale_up = {
                cooldown           = 60
                evaluation_periods = 2
                threshold          = 80
                steps = [
                  {
                    lower_bound        = 0
                    scaling_adjustment = 1
                  }
                ]
              }
              scale_down = {
                cooldown           = 60
                evaluation_periods = 3
                threshold          = 60
                steps = [
                  {
                    upper_bound        = 0
                    scaling_adjustment = -1
                  }
                ]
              }
            }
            memory = {
              enabled = true
              scale_up = {
                cooldown           = 60
                evaluation_periods = 2
                threshold          = 90
                steps = [
                  {
                    lower_bound        = 0
                    scaling_adjustment = 1
                  }
                ]
              }
              scale_down = {
                cooldown           = 60
                evaluation_periods = 3
                threshold          = 60
                steps = [
                  {
                    upper_bound        = 0
                    scaling_adjustment = -1
                  }
                ]
              }
            }
          }
        }
        """.Replace("\r\n", "\n") + "\n";

    private static string Main(Worker w, string feature, bool many)
    {
        var micro = $"worker-{w.Micro}";
        var module = many ? $"ecs-worker-task-{w.Micro}" : "ecs-worker-task";
        var sg = many ? $"worker_sg_{w.Id}" : "worker_sg";
        var iam = many ? $"iamsr_module_{w.Id}" : "iamsr_module";
        return $$"""
            module "{{module}}" {
              source = "{{WorkerModuleSource}}"

              ############## definições basicas do service
              environment                        = var.environment
              ecs_cluster_name                   = var.ecs_cluster_name
              feature_name                       = local.feature_name
              microservice_name                  = "{{micro}}"
              service_desired_count              = var.service_desired_count
              service_deployment_maximum_percent = var.service_deployment_maximum_percent
              service_launch_config              = var.service_launch_config

              ############## configurações mínimas de rede
              service_vpc_id         = var.service_vpc_id
              service_cidr_blocks    = var.service_cidr_blocks
              service_subnets        = var.service_subnets
              security_group_default = aws_security_group.{{sg}}.id
              task_container_port    = local.task_container_port
              task_health_check      = local.task_health_check

              ############## definições basicas do container
              task_cpu                           = var.task_cpu
              task_memory                        = var.task_memory
              task_ulimits_vars                  = var.task_ulimits_vars
              task_log_driver                    = var.task_log_driver
              task_ephemeral_storage_size_in_gib = var.task_ephemeral_storage_size_in_gib
              task_environment_vars              = concat(local.task_environment_vars, var.task_environment_vars)
              task_secrets                       = local.task_secrets

              ############## tags obrigatorias
              sigla               = local.sigla
              produto             = local.produto
              context             = local.context
              owner_contact_email = local.owner_contact_email
              tech_team_email     = local.tech_team_email
              finalidade          = local.finalidade
              squad               = local.squad
              github_repo_id      = var.github_repo_id
              github_repo_name    = var.github_repo_name
              build_version       = var.iupipes_build_version
              additional_tags     = local.additional_tags

              ############## roles
              task_role_arn           = module.{{iam}}.roles[1].arn
              task_execution_role_arn = module.{{iam}}.roles[1].arn

              task_runtime_platform = local.task_runtime_platform
              task_datadog          = local.task_datadog
              autoscaling           = local.autoscaling

              depends_on = [
                module.{{iam}}.roles
              ]
            }
            """.Replace("\r\n", "\n") + "\n";
    }

    private static string IamsrTemplates() => """
        resource "template_dir" "policy_template" {
          source_dir      = "iamsr/policy-template"
          destination_dir = "iamsr/policy"
          vars = {
            account_id = data.aws_caller_identity.current.account_id
            region     = data.aws_region.current.name
          }
        }

        resource "template_dir" "role_template" {
          source_dir      = "iamsr/trust-template"
          destination_dir = "iamsr/trust"
          vars = {
            account_id = data.aws_caller_identity.current.account_id
            region     = data.aws_region.current.name
          }
        }
        """.Replace("\r\n", "\n") + "\n";

    private static string IamsrModule(Worker w, bool many)
    {
        var micro = $"worker-{w.Micro}";
        var iam = many ? $"iamsr_module_{w.Id}" : "iamsr_module";
        return $$"""
            module "{{iam}}" {
              source = "{{IamsrModuleSource}}"
              policies = {
                "{{micro}}-execution-autoscale-policy" : {
                  name     = "{{micro}}-execution-autoscale-policy"
                  document = "iamsr/policy/policy-execution-autoscale.json"
                  path     = "/"
                }
                "{{micro}}-execution-ecs-policy" : {
                  name     = "{{micro}}-execution-ecs-policy"
                  document = "iamsr/policy/policy-execution-ecs.json"
                  path     = "/"
                }
                "{{micro}}-secrets-manager-policy" : {
                  name     = "{{micro}}-secrets-manager-policy-v1-0-0-1"
                  document = "iamsr/policy/policy-secrets-manager.json"
                  path     = "/"
                }
                "{{micro}}-ssm-parameter-store-policy" : {
                  name     = "{{micro}}-ssm-parameter-store-policy"
                  document = "iamsr/policy/policy-ssm-parameter-store.json"
                  path     = "/"
                }
              }
              roles = {
                # Role para autoscaling (Application Auto Scaling)
                "{{micro}}-execution-autoscale-role" : {
                  name                  = "{{micro}}-execution-autoscale-role"
                  trust_policy_document = "iamsr/trust/trust-execution-autoscale.json"
                  path                  = "/"
                  attached_policies = [
                    "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/iamsr/{{micro}}-execution-autoscale-policy"
                  ]
                }
                # Role de execução do ECS (ECR, logs, Secrets Manager, Parameter Store)
                "{{micro}}-execution-ecs-role" : {
                  name                  = "{{micro}}-execution-ecs-role"
                  trust_policy_document = "iamsr/trust/trust-execution-ecs.json"
                  path                  = "/"
                  attached_policies = [
                    "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/iamsr/{{micro}}-execution-ecs-policy",
                    "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/iamsr/{{micro}}-secrets-manager-policy-v1-0-0-1",
                    "arn:aws:iam::${data.aws_caller_identity.current.account_id}:policy/iamsr/{{micro}}-ssm-parameter-store-policy"
                  ]
                }
              }
            }
            """.Replace("\r\n", "\n") + "\n";
    }

    private static string SecurityGroup(Worker w, bool many)
    {
        var micro = $"worker-{w.Micro}";
        var sg = many ? $"worker_sg_{w.Id}" : "worker_sg";
        return $$"""
            resource "aws_security_group" "{{sg}}" {
              name        = "SG-{{micro}}"
              description = "Security group para {{micro}}"
              vpc_id      = var.service_vpc_id

              tags = merge(
                local.additional_tags,
                {
                  Name = "SG-{{micro}}"
                }
              )
            }

            # Ingress: health check na porta do container, a partir dos CIDRs informados.
            resource "aws_security_group_rule" "{{sg}}_ingress" {
              type              = "ingress"
              from_port         = local.default_container_port
              to_port           = local.default_container_port
              protocol          = "tcp"
              cidr_blocks       = var.service_cidr_blocks
              security_group_id = aws_security_group.{{sg}}.id
            }

            # Egress: todo o tráfego de saída (banco, Exchange, APIs corporativas).
            resource "aws_security_group_rule" "{{sg}}_egress" {
              type              = "egress"
              from_port         = 0
              to_port           = 0
              protocol          = "-1"
              cidr_blocks       = ["0.0.0.0/0"]
              security_group_id = aws_security_group.{{sg}}.id
              description       = "Egress - Permitir todo o trafego de saida"
            }
            """.Replace("\r\n", "\n") + "\n";
    }

    private static string Outputs(Worker w, bool many)
    {
        var module = many ? $"ecs-worker-task-{w.Micro}" : "ecs-worker-task";
        var prefix = many ? w.Id + "_" : "";
        var sb = new StringBuilder();
        foreach (var (name, sensitive) in new[] { ("task_definition", true), ("appspec", true), ("service_name", false), ("cluster_name", false), ("codedeploy_application", false), ("codedeploy_group", false), ("codedeploy_strategy", false) })
        {
            sb.AppendLine($"output \"{prefix}{name}\" {{");
            sb.AppendLine($"  value{(sensitive ? "    " : "")} = module.{module}.{name}");
            if (sensitive) sb.AppendLine("  sensitive = true");
            sb.AppendLine("}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ------------------------------------------------------------------ IAM documents

    private static Dictionary<string, string> IamsrDocuments(string secretName)
    {
        var docs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["iamsr/policy/.gitkeep"] = "",
            ["iamsr/trust/.gitkeep"] = "",
            ["iamsr/policy-template/policy-execution-autoscale.json"] = """
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    {
                      "Effect": "Allow",
                      "Action": ["cloudwatch:DeleteAlarms", "cloudwatch:DescribeAlarms", "cloudwatch:PutMetricAlarm"],
                      "Resource": ["arn:aws:cloudwatch:*:*:alarm:*"],
                      "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                    },
                    {
                      "Effect": "Allow",
                      "Action": [
                        "application-autoscaling:DescribeScalableTargets",
                        "application-autoscaling:DescribeScalingActivities",
                        "application-autoscaling:DescribeScalingPolicies",
                        "application-autoscaling:DescribeScheduledActions",
                        "application-autoscaling:DeleteScalingPolicy",
                        "application-autoscaling:DeleteScheduledAction",
                        "application-autoscaling:DeregisterScalableTarget",
                        "application-autoscaling:PutScalingPolicy",
                        "application-autoscaling:PutScheduledAction",
                        "application-autoscaling:RegisterScalableTarget"
                      ],
                      "Resource": "*",
                      "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                    },
                    {
                      "Effect": "Allow",
                      "Action": ["ecs:DescribeServices", "ecs:UpdateService"],
                      "Resource": ["arn:aws:ecs:*:*:service/*/*"],
                      "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                    }
                  ]
                }
                """,
            ["iamsr/policy-template/policy-execution-ecs.json"] = ExecutionEcsPolicy(),
            ["iamsr/policy-template/policy-no-role-chaining.json"] = """
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    { "Effect": "Deny", "Action": "sts:AssumeRole", "Resource": "*" }
                  ]
                }
                """,
            ["iamsr/policy-template/policy-secrets-manager.json"] = $$"""
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    {
                      "Sid": "SecretsManagerRead",
                      "Effect": "Allow",
                      "Action": ["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"],
                      "Resource": [
                        "arn:aws:secretsmanager:${region}:${account_id}:secret:RDSInstanceEndpointSecret-*",
                        "arn:aws:secretsmanager:${region}:${account_id}:secret:RDSInstanceUserSecret-*",
                        "arn:aws:secretsmanager:${region}:${account_id}:secret:RDSInstancePasswordSecret-*",
                        "arn:aws:secretsmanager:${region}:${account_id}:secret:{{secretName}}-*"
                      ]
                    }
                  ]
                }
                """,
            ["iamsr/policy-template/policy-ssm-parameter-store.json"] = """
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    {
                      "Sid": "SsmGetParameter",
                      "Effect": "Allow",
                      "Action": ["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath"],
                      "Resource": "arn:aws:ssm:*:*:parameter/*"
                    }
                  ]
                }
                """,
            ["iamsr/trust-template/trust-execution-autoscale.json"] = """
                {
                  "Version": "2012-10-17",
                  "Statement": {
                    "Effect": "Allow",
                    "Principal": { "Service": ["application-autoscaling.amazonaws.com"] },
                    "Action": ["sts:AssumeRole"]
                  }
                }
                """,
            ["iamsr/trust-template/trust-execution-ecs.json"] = """
                {
                  "Version": "2012-10-17",
                  "Statement": {
                    "Effect": "Allow",
                    "Principal": { "Service": ["ecs-tasks.amazonaws.com"] },
                    "Action": ["sts:AssumeRole", "ecs:RunTask"]
                  }
                }
                """
        };
        return docs.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("\r\n", "\n").TrimEnd() is { Length: > 0 } t ? t + "\n" : "");
    }

    private static string ExecutionEcsPolicy()
    {
        var ecr = string.Join(",\n", new[] { "\"arn:aws:ecr:${region}:${account_id}:repository/*\"" }.Concat(PlatformEcrAccounts.Select(a => $"        \"arn:aws:ecr:${{region}}:{a}:repository/*\"")));
        var accounts = string.Join(",\n", PlatformEcrAccounts.Select(a => $"            \"{a}\"").Append("            \"${account_id}\""));
        return $$"""
            {
              "Version": "2012-10-17",
              "Statement": [
                {
                  "Effect": "Allow",
                  "Action": ["ecr:BatchCheckLayerAvailability", "ecr:GetDownloadUrlForLayer", "ecr:BatchGetImage"],
                  "Resource": [
                    {{ecr}}
                  ]
                },
                {
                  "Effect": "Allow",
                  "Action": ["ecr:GetAuthorizationToken"],
                  "Resource": "*",
                  "Condition": {
                    "StringEquals": {
                      "aws:ResourceAccount": [
            {{accounts}}
                      ]
                    }
                  }
                },
                {
                  "Effect": "Allow",
                  "Action": ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents", "logs:DescribeLogStreams"],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": [
                    "ssm:UpdateInstanceInformation", "ssm:ListInstanceAssociations", "ssm:DescribeInstanceProperties",
                    "ssm:DescribeDocumentParameters", "ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath"
                  ],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": ["kms:DescribeKey", "kms:ListAliases", "kms:ListKeys", "kms:Encrypt", "kms:Decrypt", "kms:ReEncrypt*", "kms:GenerateDataKey*"],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": [
                    "ecs:DiscoverPollEndpoint", "ecs:RegisterContainerInstance", "ecs:SubmitContainerStateChange", "ecs:SubmitTaskStateChange",
                    "ecs:ListContainerInstances", "ecs:Poll", "ecs:StartTelemetrySession", "ecs:UpdateContainerInstancesState",
                    "ecs:ListTasks", "ecs:DescribeContainerInstances", "ecs:ExecuteCommand"
                  ],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": ["cloudwatch:PutMetricAlarm", "cloudwatch:PutDashboard", "cloudwatch:PutMetricData", "cloudwatch:GetMetricData", "cloudwatch:ListMetrics"],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": ["iam:PassRole"],
                  "Resource": ["arn:aws:ecs:*:*:container-instance/*", "arn:aws:ecs:*:*:cluster/*"],
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": ["ecs:RunTask"],
                  "Resource": "*",
                  "Condition": { "StringEquals": { "aws:ResourceAccount": "${account_id}" } }
                },
                {
                  "Effect": "Allow",
                  "Action": ["iam:PassRole"],
                  "Resource": ["arn:aws:iam::${account_id}:role/*-execution-ecs-role"]
                }
              ]
            }
            """;
    }

    // ------------------------------------------------------------------ inventories

    private static string TfVars(SolutionResult result, List<Worker> workers, string environment, string secretName)
    {
        var dotnetEnvironment = environment switch { "dev" => "Development", "hom" => "Staging", _ => "Production" };
        var sb = new StringBuilder();
        sb.AppendLine("############## definições basicas do service");
        sb.AppendLine($"environment        = \"{environment}\"");
        sb.AppendLine($"dotnet_environment = \"{dotnetEnvironment}\"");
        sb.AppendLine("ecs_cluster_name   = \"PREENCHER-cluster-ecs\"");
        sb.AppendLine();
        if (workers.Any(w => w.Profile.HasAny(Signal.MailboxReading, Signal.FileWatcher, Signal.Ftp)))
            sb.AppendLine("# caixa de e-mail/pasta: mantenha UMA task, senão o mesmo item é processado duas vezes");
        sb.AppendLine("service_desired_count = 1");
        sb.AppendLine("service_min_capacity  = 1");
        sb.AppendLine($"service_max_capacity  = {(workers.Any(w => w.Profile.HasAny(Signal.MailboxReading, Signal.FileWatcher, Signal.Ftp)) ? 1 : 2)}");
        sb.AppendLine();
        sb.AppendLine("service_launch_config = {");
        sb.AppendLine("  launch_type = \"FARGATE\"");
        if (environment != "prod")
        {
            sb.AppendLine("  capacity_providers = [");
            sb.AppendLine("    {");
            sb.AppendLine("      capacity_provider = \"FARGATE_SPOT\"");
            sb.AppendLine("      base              = 1");
            sb.AppendLine("      weight            = 1");
            sb.AppendLine("    }");
            sb.AppendLine("  ]");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("############## configurações mínimas de rede (compartilhadas: nunca criadas aqui; PREENCHER VPC, subnets e os CIDRs que acessam o /health)");
        sb.AppendLine("service_vpc_id      = \"vpc-PREENCHER\"");
        sb.AppendLine("service_cidr_blocks = [\"10.0.0.0/8\"]");
        sb.AppendLine("service_subnets     = [\"subnet-PREENCHER1\", \"subnet-PREENCHER2\", \"subnet-PREENCHER3\"]");
        sb.AppendLine();
        sb.AppendLine("############## definições basicas do container");
        sb.AppendLine("task_cpu    = 512");
        sb.AppendLine("task_memory = 1024");
        sb.AppendLine("task_ulimits_vars = [");
        sb.AppendLine("  { name = \"nofile\", softLimit = 2048, hardLimit = 8192 }");
        sb.AppendLine("]");
        sb.AppendLine(environment == "prod"
            ? "task_log_driver = { log-driver = \"awsfirelens\", cloudwatch-retention-logs-in-days = 30 }"
            : "task_log_driver = { log-driver = \"awslogs\", options = { awslogs-create-group = \"false\", awslogs-retention-in-days = 1 } }");
        sb.AppendLine();
        sb.AppendLine("############## segredos (só o ARN e os nomes das chaves; valores em _secrets/platform/)");
        sb.AppendLine($"application_secret_arn = \"arn:aws:secretsmanager:sa-east-1:{PlaceholderAccount}:secret:{secretName}-PREENCHER\"");
        sb.AppendLine("secret_kms_key_arn     = \"\"");
        var secrets = SecretsOf(workers).Where(s => AppliesTo(s, environment)).Select(s => s.EnvironmentVariable).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        sb.AppendLine("secret_keys = {");
        var keyWidth = secrets.Count == 0 ? 0 : secrets.Max(k => k.Length);
        foreach (var key in secrets) sb.AppendLine($"  \"{key}\"{new string(' ', keyWidth - key.Length)} = \"{key}\"");
        sb.AppendLine("}");
        sb.AppendLine();

        var envNormalized = environment switch { "dev" => "Development", "hom" => "Staging", _ => "Production" };
        var environmentVariables = workers.SelectMany(w => w.Result.EnvironmentVariablesRead.Select(v => (Variable: v, Worker: w))).DistinctBy(x => x.Variable).ToList();
        if (environmentVariables.Count > 0)
        {
            sb.AppendLine("############## variáveis de ambiente que o código lê diretamente");
            sb.AppendLine("# Lidas pelo código com Environment.GetEnvironmentVariable; quando o valor é o nome do arquivo de configuração (Resources/<valor>.json) já vem preenchido.");
            sb.AppendLine("app_environment = {");
            foreach (var (variable, worker) in environmentVariables)
            {
                var names = worker.Result.ConfigEnvironmentNames;
                var value = names.TryGetValue(envNormalized, out var n) ? n : names.TryGetValue("Development", out var d) && environment != "prod" ? d : null;
                sb.AppendLine($"  \"{variable}\" = \"{Hcl(value ?? "PREENCHER")}\"");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        var settings = workers.SelectMany(w => w.Result.SettingsWithDependencies).Where(s => s.Kind != SettingKind.Secret)
            .DistinctBy(s => s.EnvironmentVariable, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.EnvironmentVariable, StringComparer.Ordinal)
            .Select(s => (Setting: s, Value: SettingValue(s, environment))).Where(x => x.Value != null).ToList();
        sb.AppendLine("############## URLs, e-mails e pastas que estavam fixos no código/JSON (um valor por ambiente)");
        var networkPaths = settings.Where(x => x.Setting.Kind == SettingKind.Path).Select(x => x.Setting.EnvironmentVariable).ToList();
        if (networkPaths.Count > 0)
            sb.AppendLine($"# ATENÇÃO: pastas de rede (UNC do Windows) não existem no container Linux; use EFS ou S3 e troque o valor: {string.Join(", ", networkPaths)}");
        sb.AppendLine("app_settings = {");
        var width = settings.Count == 0 ? 0 : settings.Max(x => x.Setting.EnvironmentVariable.Length);
        foreach (var (s, value) in settings)
            sb.AppendLine($"  \"{s.EnvironmentVariable}\"{new string(' ', width - s.EnvironmentVariable.Length)} = \"{Hcl(value!)}\"");
        sb.AppendLine("}");
        return AlignAssignments(sb.ToString().Replace("\r\n", "\n"));
    }

    /// <summary>Config transforms name environments the .NET way; map them to the deploy environments.</summary>
    private static string? SettingValue(ExternalizedSetting setting, string environment)
    {
        var names = environment switch
        {
            "dev" => new[] { "Development" },
            "prod" => ["Production"],
            _ => ["Staging", "Homolog", "Homologacao", "Hom", "Hml", "Test", "Development"]
        };
        foreach (var name in names) if (setting.EnvironmentValues.TryGetValue(name, out var value)) return value;
        return setting.EnvironmentOnly ? null : setting.Value;
    }

    /// <summary>terraform fmt aligns consecutive attributes: the generated lines already carry the padding, this only normalizes the comment-trailing ones.</summary>
    private static string AlignAssignments(string hcl) => hcl;

    /// <summary>HCL double-quoted string: backslashes, quotes and interpolation openers escaped.</summary>
    internal static string Hcl(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("${", "$${").Replace("%{", "%%{").Replace("\r", "").Replace("\n", "\\n");

    // ------------------------------------------------------------------ container image, pipeline

    private static string Dockerfile(Worker w) => $$"""
        # Gerado pelo Migrator (--iac terraform-platform) para {{w.Result.Project.Name}}: imagem do worker ECS (Linux ARM64/Graviton).
        # A esteira roda 'dotnet publish' antes e coloca o resultado em ./app (relativo a este arquivo); a imagem só empacota o publish.
        FROM amazon/aws-cli:latest AS certs
        ARG AWS_DEFAULT_REGION
        ARG AWS_ACCESS_KEY_ID
        ARG AWS_SECRET_ACCESS_KEY
        ARG AWS_SESSION_TOKEN
        RUN mkdir -p /opt/certs
        RUN aws s3 cp s3://cloudsec-ca-certs-bundle-prod-sa-east-1/ca_bundle.crt /opt/certs/ca_bundle.crt

        FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine-arm64v8

        # ENV's dotnet
        ENV ASPNETCORE_URLS=http://+:{{ContainerPort}}
        ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
        # O Windows de origem rodava em horário local; sem TZ o container usa UTC (veja MOD-CS-DATETIME-NOW).
        ENV TZ=America/Sao_Paulo

        ADD ./app /app/out/

        # Usuário não-root
        ARG USERNAME=dotnet_user
        RUN addgroup -S "$USERNAME" && adduser -S "$USERNAME" -G "$USERNAME"
        RUN mkdir -p /var/log
        RUN chown -R "$USERNAME":"$USERNAME" /app/
        RUN chown -R "$USERNAME":"$USERNAME" /var/log/

        # Pacotes, certificados e limpeza de cache em uma camada
        RUN apk add --no-cache busybox --repository=http://dl-cdn.alpinelinux.org/alpine/edge/main
        RUN apk update && \
            apk upgrade && \
            apk add --no-cache unzip && \
            apk add --no-cache curl && \
            apk add --no-cache tzdata && \
            apk add --no-cache icu-libs && \
            apk add --no-cache icu-data-full && \
            apk add --no-cache ca-certificates && \
            apk add --no-cache musl && \
            apk add --no-cache musl-utils && \
            apk add --no-cache musl-locales && \
            apk add --no-cache musl-locales-lang && \
            apk upgrade zlib && \
            apk upgrade krb5-libs && \
            apk upgrade libcrypto3 && \
            apk upgrade libssl3 && \
            rm -rf /var/cache/apk/*

        # Certificados corporativos
        COPY --from=certs /opt/certs /usr/local/share/ca-certificates
        RUN update-ca-certificates

        # Datadog APM
        ENV URL_DD_TRACE=https://github.com/DataDog/dd-trace-dotnet/releases/download/v3.38.0/datadog-dotnet-apm-3.38.0.arm64.tar.gz
        RUN mkdir -p /opt/datadog
        ADD $URL_DD_TRACE /tmp/datadog-dotnet-apm_arm64.tar.gz
        RUN tar -C /opt/datadog -xzf /tmp/datadog-dotnet-apm_arm64.tar.gz && sh /opt/datadog/createLogPath.sh
        RUN chown -R dotnet_user /opt/datadog && chmod -R 755 /opt/datadog
        ENV CORECLR_ENABLE_PROFILING=1
        ENV CORECLR_PROFILER={846F5F1C-F9AE-4B07-969E-05C26BC060D8}
        ENV CORECLR_PROFILER_PATH=/opt/datadog/Datadog.Trace.ClrProfiler.Native.so
        ENV DD_DOTNET_TRACER_HOME=/opt/datadog
        ENV DD_RUNTIME_METRICS_ENABLED=true

        EXPOSE {{ContainerPort}}

        USER dotnet_user
        WORKDIR /app/out
        ENTRYPOINT ["dotnet", "{{w.Result.Project.AssemblyName}}.dll"]
        """.Replace("\r\n", "\n") + "\n";

    private static string Iupipes(SolutionResult result, List<Worker> workers, string feature)
    {
        var first = workers[0];
        var sb = new StringBuilder();
        sb.AppendLine("#Documentação: https://ideal-bassoon-12961ef9.pages.github.io/jornadas/cloud-publica/aws/ecs/dotnet-core/");
        sb.AppendLine("project:");
        sb.AppendLine("  language: 'dotnet-core'");
        sb.AppendLine();
        sb.AppendLine("infra:");
        sb.AppendLine("  terraform:");
        sb.AppendLine("    # Obrigatorio");
        sb.AppendLine("    destroy: \"false\"");
        sb.AppendLine("    working-directory: 'infra/terraform'");
        sb.AppendLine("    version: \"1.5.7\"");
        sb.AppendLine("    aws-statefile-acl: \"private\"");
        sb.AppendLine("    aws-s3-data-retention: 0");
        sb.AppendLine("    aws-s3-data-classification: \"Interna\"");
        sb.AppendLine("    aws-tech-team-email: \"time@empresa.com.br\"");
        sb.AppendLine("    aws-owner-contact-email: \"po@empresa.com.br\"");
        sb.AppendLine();
        sb.AppendLine("build:");
        sb.AppendLine("  # Obrigatorio. Os valores de FEATURE_NAME e MICROSERVICE_NAME devem ser iguais aos do main.tf / locals.tf");
        sb.AppendLine("  version: 10.0.x");
        sb.AppendLine("  docker-platform: 'linux/arm64'");
        sb.AppendLine("  parameters: '/p:PublishWithAspNetCoreTargetManifest=false /p:RunCodeAnalysis=false'");
        sb.AppendLine("  restore-parameters: ''");
        sb.AppendLine($"  working-directory: './{CloudFormationGenerator.SourceDir}'");
        sb.AppendLine($"  nuget-config-path: './{CloudFormationGenerator.SourceDir}'");
        sb.AppendLine();
        sb.AppendLine("unit-tests:");
        sb.AppendLine("  directory: '.'");
        sb.AppendLine();
        sb.AppendLine("# Obrigatorio");
        sb.AppendLine("deploy:");
        sb.AppendLine("  aws:");
        sb.AppendLine("    noloadbalancer: true");
        foreach (var environment in Environments)
        {
            sb.AppendLine($"    {environment}:");
            sb.AppendLine($"      account: \"ACCOUNT_ID_{environment.ToUpperInvariant()}\"     # Conta AWS de {environment.ToUpperInvariant()}.");
            sb.AppendLine("      region:  \"sa-east-1\"");
            sb.AppendLine("      deployment-type: 'in-place'");
            sb.AppendLine("      ecs:");
            sb.AppendLine($"        service: 'service-{first.Micro}'");
            sb.AppendLine("        cluster: 'PREENCHER-cluster-ecs'");
            sb.AppendLine($"        application: '{first.Micro}'   # começa com letra, sem maiúsculas");
            sb.AppendLine($"        deployment-group: '{first.Micro}'");
            sb.AppendLine($"        container-name: 'container-{first.Micro}'");
        }
        sb.AppendLine();
        sb.AppendLine("security:");
        sb.AppendLine("  fortify:");
        sb.AppendLine("    environment-analyse: 'pipelinescan'");
        sb.AppendLine("    sigla-app: 'SIGLA-APP'");
        sb.AppendLine("    sigla: 'SIGLA'");
        sb.AppendLine($"    nome-aws: '{first.Micro}'");
        sb.AppendLine($"    produto-aws: '{feature}'");
        sb.AppendLine();
        sb.AppendLine("quality:");
        sb.AppendLine("  sonar:");
        sb.AppendLine("    language: dotnet-core");
        sb.AppendLine($"    working-directory: './{CloudFormationGenerator.SourceDir}'");
        sb.AppendLine($"    nugetConfigPath: './{CloudFormationGenerator.SourceDir}'");
        sb.AppendLine("    parameters: ''");
        sb.AppendLine("    gate-name: ''");
        sb.AppendLine();
        foreach (var tool in new[] { "taac", "p4all" })
        {
            sb.AppendLine($"{tool}:");
            foreach (var environment in new[] { "dev", "hom" })
            {
                sb.AppendLine($"  {environment}:");
                sb.AppendLine("    failOnError: false");
                sb.AppendLine("    codebuild:");
                sb.AppendLine("      computeType: \"BUILD_GENERAL1_SMALL\"");
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd().Replace("\r\n", "\n") + "\n";
    }

    private static string TestSpec() => """
        version: 0.2

        phases:
          build:
            commands:
              - echo 'Testes executados manualmente'
              # Para automatizar: dotnet test app/src e um smoke test da URL publicada (curl -f https://<host>/health)
        """.Replace("\r\n", "\n") + "\n";

    private static string Readme(SolutionResult result, List<Worker> workers, List<ProjectResult> notGenerated, string secretName, List<ExtractedSecret> secrets)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Infraestrutura (Terraform, módulos da plataforma)");
        sb.AppendLine();
        sb.AppendLine("Gerado pelo Migrator com `--iac terraform-platform`. Cada ambiente é um arquivo em `terraform/inventories/<ambiente>/terraform.tfvars`; a esteira (`.iupipes.yml`, bloco `infra.terraform`) roda `init/plan/apply` em `infra/terraform`.");
        sb.AppendLine();
        sb.AppendLine("| Worker | Módulo | Serviço ECS |");
        sb.AppendLine("|---|---|---|");
        foreach (var w in workers) sb.AppendLine($"| {w.Result.Project.Name} | `modules/worker` (itau-hn8-modules-ecs-service) | `service-{w.Micro}` / container `container-{w.Micro}` |");
        sb.AppendLine();
        sb.AppendLine("## Antes do primeiro deploy");
        sb.AppendLine();
        sb.AppendLine("1. Preencher os `PREENCHER` de `inventories/{dev,hom,prod}/terraform.tfvars` (cluster, VPC, subnets, CIDRs) e de `locals.tf` (sigla, produto, squad, e-mails).");
        if (secrets.Count > 0)
            sb.AppendLine($"2. Criar o segredo `{secretName}` em cada conta com `_secrets/platform/create-platform-secret.sh <ambiente>` e colocar o ARN em `application_secret_arn`. Chaves: {string.Join(", ", secrets.Select(s => $"`{s.EnvironmentVariable}`").Distinct())}. Rotacione as credenciais antigas.");
        else
            sb.AppendLine("2. Nenhuma credencial foi encontrada; se a aplicação usa alguma, crie o segredo JSON e preencha `application_secret_arn` e `secret_keys`.");
        sb.AppendLine("3. Revisar `app_settings` (URLs, e-mails e pastas por ambiente) e `app_environment` (variáveis que o código lê). Pastas de rede (UNC) não existem no container Linux: use EFS ou S3.");
        sb.AppendLine("4. Preencher `.iupipes.yml` (contas, sigla, cluster, e-mails) e abrir o repositório na esteira (nunca subir `_secrets/`).");
        sb.AppendLine($"5. O módulo worker expõe o health check em `http://localhost:{ContainerPort}/health`; o worker gerado já responde nessa rota.");
        if (workers.Count > 1) sb.AppendLine("6. Há mais de um worker: o `.iupipes.yml` cobre o primeiro; duplique o bloco `deploy.aws.<env>.ecs` para os demais (Dockerfile.<micro> por serviço).");
        if (notGenerated.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Não gerados");
            sb.AppendLine();
            foreach (var p in notGenerated) sb.AppendLine($"- **{p.Project.Name}** ({p.Hosting!.Primary.Display()}): os módulos Terraform da plataforma usados aqui cobrem apenas workers ECS; use o template da plataforma para este tipo de serviço.");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ------------------------------------------------------------------ naming

    private static string Id(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');

    /// <summary>Microservice name: project without the solution prefix, lowercase words separated by hyphens ("Fenaseg.Desmembramento" → "desmembramento").</summary>
    private static string Micro(SolutionResult result, string projectName)
    {
        var name = projectName.StartsWith(result.SolutionName + ".", StringComparison.OrdinalIgnoreCase) ? projectName[(result.SolutionName.Length + 1)..] : projectName;
        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "worker" : slug;
    }
}
