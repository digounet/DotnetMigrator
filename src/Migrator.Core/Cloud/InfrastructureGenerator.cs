using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// Turns the <see cref="ArchitectureProposal"/> into deployable artifacts: a Terraform root module (infra/terraform/)
/// and a GitHub Actions workflow (.github/workflows/deploy.yml). Everything is parameterized by variables with sane
/// defaults so the same template serves every application of the portfolio; nothing here contains credentials.
/// </summary>
public static partial class InfrastructureGenerator
{
    public const string TerraformDir = "infra/terraform";

    private sealed record Deployable(ProjectResult Result, ApplicationProfile Profile, string Slug, string Id);

    public static Dictionary<string, string> Generate(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var arch = result.Architecture;
        if (arch == null) return files;
        var components = arch.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var app = Slug(result.SolutionName);
        var deployables = profiles
            .Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath != null)
            .Select(p => new Deployable(p.Result, p.Profile, Slug(p.Result.Project.Name), Id(p.Result.Project.Name)))
            .ToList();
        if (deployables.Count == 0) return files;

        var webs = deployables.Where(d => d.Result.Project.Kind == ProjectKind.Web && d.Result.Hosting!.Primary != AwsHosting.Ec2Windows).ToList();
        var scheduled = deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask).ToList();
        var workers = deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker).ToList();
        var lambdas = deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.Lambda).ToList();
        var windows = deployables.Where(d => d.Result.Hosting!.Primary is AwsHosting.EcsWindows).ToList();
        var containers = webs.Concat(scheduled).Concat(workers).Concat(windows).ToList();
        var ec2 = deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.Ec2Windows).ToList();

        files[$"{TerraformDir}/versions.tf"] = Versions();
        files[$"{TerraformDir}/variables.tf"] = Variables(app, webs, scheduled, lambdas, result);
        files[$"{TerraformDir}/network.tf"] = Network(components);
        if (containers.Count > 0 || lambdas.Count > 0) files[$"{TerraformDir}/iam.tf"] = Iam(components, deployables);
        if (containers.Count > 0) files[$"{TerraformDir}/ecs.tf"] = Ecs(app, webs, scheduled, workers, windows, result);
        if (webs.Count > 0) files[$"{TerraformDir}/alb.tf"] = Alb(webs);
        if (lambdas.Count > 0) files[$"{TerraformDir}/lambda.tf"] = Lambda(lambdas, components);
        var data = Data(result, components);
        if (data.Length > 0) files[$"{TerraformDir}/data.tf"] = data;
        var storage = Storage(components, deployables, workers.Concat(lambdas).ToList());
        if (storage.Length > 0) files[$"{TerraformDir}/storage.tf"] = storage;
        if (components.Contains("elasticache")) files[$"{TerraformDir}/cache.tf"] = Cache();
        files[$"{TerraformDir}/observability.tf"] = Observability(webs, containers);
        files[$"{TerraformDir}/outputs.tf"] = Outputs(webs, containers, lambdas, components, result);
        files[$"{TerraformDir}/terraform.tfvars.example"] = TfVarsExample(app, webs, result);
        var notGenerated = profiles.Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath == null).Select(p => p.Result).ToList();
        files["infra/README.md"] = Readme(result, deployables, lambdas, ec2, notGenerated, components);
        files[".github/workflows/deploy.yml"] = Workflow(app, containers, lambdas, result);
        foreach (var key in files.Keys.Where(k => k.EndsWith(".tf", StringComparison.Ordinal)).ToList()) files[key] = AlignHcl(files[key]);
        return files;
    }

    /// <summary>Mimics `terraform fmt`: consecutive `key = value` lines at the same indentation get their `=` aligned.</summary>
    internal static string AlignHcl(string hcl)
    {
        var lines = hcl.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>(lines.Length);
        var group = new List<(string Indent, string Key, string Value)>();
        void Flush()
        {
            if (group.Count == 0) return;
            var width = group.Max(g => g.Key.Length);
            foreach (var (indent, key, value) in group) output.Add($"{indent}{key.PadRight(width)} = {value}");
            group.Clear();
        }
        foreach (var line in lines)
        {
            var m = AttributeLine().Match(line);
            // An attribute whose value opens a multi-line list/object/call is not aligned with its neighbours (terraform fmt behaviour).
            var opensBlock = m.Success && m.Groups["value"].Value.TrimEnd() is var v && (v.EndsWith('[') || v.EndsWith('{') || v.EndsWith('('));
            if (m.Success && !opensBlock && (group.Count == 0 || group[0].Indent == m.Groups["indent"].Value))
            {
                group.Add((m.Groups["indent"].Value, m.Groups["key"].Value, m.Groups["value"].Value));
                continue;
            }
            Flush();
            if (m.Success && !opensBlock) { group.Add((m.Groups["indent"].Value, m.Groups["key"].Value, m.Groups["value"].Value)); continue; }
            output.Add(m.Success ? $"{m.Groups["indent"].Value}{m.Groups["key"].Value} = {m.Groups["value"].Value}" : line);
        }
        Flush();
        return string.Join("\n", output);
    }

    [GeneratedRegex(@"^(?<indent>\s*)(?<key>[A-Za-z_][\w\-]*|""[^""]+"")\s*=\s*(?<value>(?!=).+?)\s*$")]
    private static partial Regex AttributeLine();

    // ------------------------------------------------------------------ Terraform files

    private static string Versions() => """
        terraform {
          required_version = ">= 1.6"
          required_providers {
            aws    = { source = "hashicorp/aws", version = ">= 5.70" }
            random = { source = "hashicorp/random", version = ">= 3.6" }
          }
          # Estado remoto (recomendado): descomente e ajuste o bucket/tabela antes do primeiro apply.
          # backend "s3" {
          #   bucket         = "minha-empresa-terraform-state"
          #   key            = "APP/ENV/terraform.tfstate"
          #   region         = "sa-east-1"
          #   dynamodb_table = "terraform-locks"
          #   encrypt        = true
          # }
        }

        provider "aws" {
          region = var.aws_region
          default_tags {
            tags = local.tags
          }
        }

        data "aws_caller_identity" "current" {}
        data "aws_availability_zones" "available" {
          state = "available"
        }

        locals {
          name = "${var.app_name}-${var.environment}"
          tags = {
            Application = var.app_name
            Environment = var.environment
            ManagedBy   = "terraform"
            Generator   = "migrator"
          }
        }
        """.Replace("\r\n", "\n");

    private static string Variables(string app, List<Deployable> webs, List<Deployable> scheduled, List<Deployable> lambdas, SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            variable "aws_region" {
              description = "Região AWS."
              type        = string
              default     = "sa-east-1"
            }

            variable "environment" {
              description = "Ambiente (dev, hml, prod): compõe o nome dos recursos."
              type        = string
              default     = "prod"
            }

            variable "app_name" {
              description = "Nome da aplicação (prefixo dos recursos)."
              type        = string
              default     = "{{app}}"
            }

            variable "vpc_cidr" {
              description = "CIDR da VPC."
              type        = string
              default     = "10.40.0.0/16"
            }

            variable "image_tag" {
              description = "Tag das imagens no ECR (o pipeline usa o SHA do commit)."
              type        = string
              default     = "latest"
            }

            variable "certificate_arn" {
              description = "ARN de um certificado no ACM para HTTPS no ALB. Vazio = só HTTP (apenas para testes)."
              type        = string
              default     = ""
            }

            variable "alert_email" {
              description = "E-mail que recebe os alarmes do CloudWatch (vazio = sem assinatura)."
              type        = string
              default     = ""
            }
            """);
        if (webs.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("variable \"web_hosts\" {");
            sb.AppendLine("  description = \"Host header de cada aplicação web no ALB (um ALB serve todas).\"");
            sb.AppendLine("  type        = map(string)");
            sb.AppendLine("  default = {");
            foreach (var w in webs) sb.AppendLine($"    {w.Id} = \"{w.Slug}.exemplo.com.br\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("variable \"web_desired_count\" {");
            sb.AppendLine("  description = \"Tasks por serviço web (2 = alta disponibilidade entre AZs).\"");
            sb.AppendLine("  type        = number");
            sb.AppendLine("  default     = 2");
            sb.AppendLine("}");
        }
        if (scheduled.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("variable \"schedules\" {");
            sb.AppendLine("  description = \"Expressão do EventBridge Scheduler por tarefa agendada (cron(...) ou rate(...)), em UTC.\"");
            sb.AppendLine("  type        = map(string)");
            sb.AppendLine("  default = {");
            foreach (var s in scheduled) sb.AppendLine($"    {s.Id} = \"rate(15 minutes)\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }
        if (lambdas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("variable \"lambda_runtime\" {");
            sb.AppendLine("  description = \"Runtime gerenciado do Lambda para .NET (ajuste se a região ainda não oferecer o .NET 10).\"");
            sb.AppendLine("  type        = string");
            sb.AppendLine("  default     = \"dotnet10\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("variable \"lambda_packages\" {");
            sb.AppendLine("  description = \"Caminho do .zip de cada função (saída de 'dotnet lambda package').\"");
            sb.AppendLine("  type        = map(string)");
            sb.AppendLine("  default = {");
            foreach (var l in lambdas) sb.AppendLine($"    {l.Id} = \"../../{l.Result.RelativeDir.Replace('\\', '/')}/bin/Release/net10.0/{l.Result.Project.AssemblyName}.zip\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            if (lambdas.Any(l => l.Profile.Has(Signal.MailboxReading)))
            {
                sb.AppendLine();
                sb.AppendLine("variable \"enable_ses_inbound\" {");
                sb.AppendLine("  description = \"Cria o recebimento de e-mail pelo SES (exige domínio verificado e MX apontando para o SES).\"");
                sb.AppendLine("  type        = bool");
                sb.AppendLine("  default     = false");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine("variable \"inbound_recipients\" {");
                sb.AppendLine("  description = \"Endereços/domínios recebidos pelo SES (ex.: pedidos@exemplo.com.br).\"");
                sb.AppendLine("  type        = list(string)");
                sb.AppendLine("  default     = []");
                sb.AppendLine("}");
            }
        }
        var settings = result.Projects.SelectMany(p => p.SettingsWithDependencies).Where(s => s.Kind != SettingKind.Secret).DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        if (settings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("variable \"app_settings\" {");
            sb.AppendLine("  description = \"URLs e e-mails que estavam fixos no código/appSettings: injetados como variáveis de ambiente (AppSettings__Secao__Chave); um valor por ambiente no tfvars. Segredos não entram aqui.\"");
            sb.AppendLine("  type        = map(string)");
            sb.AppendLine("  default = {");
            foreach (var setting in settings)
            {
                // Keys with a dot (AppSettings__Smtp.From) are not HCL identifiers: quote them.
                var key = Regex.IsMatch(setting.EnvironmentVariable, @"^[A-Za-z_][\w\-]*$") ? setting.EnvironmentVariable : $"\"{setting.EnvironmentVariable}\"";
                sb.AppendLine($"    {key} = \"{setting.Value.Replace("\"", "\\\"")}\"");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }
        if (result.Databases.Count > 0)
        {
            var sqlServer = result.Databases.Any(d => d.Provider == "SQL Server");
            sb.AppendLine();
            sb.AppendLine("variable \"db_engine\" {");
            sb.AppendLine($"  description = \"Engine do RDS{(sqlServer ? ": sqlserver-ex (gratuito em licença, até 10 GB/base), sqlserver-web, sqlserver-se, sqlserver-ee" : "")}.\"");
            sb.AppendLine("  type        = string");
            sb.AppendLine($"  default     = \"{DefaultEngine(result)}\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("variable \"db_instance_class\" {");
            sb.AppendLine("  type    = string");
            sb.AppendLine($"  default = \"{(sqlServer ? "db.t3.small" : "db.t4g.medium")}\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("variable \"db_allocated_storage\" {");
            sb.AppendLine("  type    = number");
            sb.AppendLine("  default = 50");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("variable \"db_username\" {");
            sb.AppendLine("  type    = string");
            sb.AppendLine("  default = \"appadmin\"");
            sb.AppendLine("}");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string DefaultEngine(SolutionResult result) => result.Databases.Select(d => d.Provider).FirstOrDefault() switch
    {
        "Oracle" => "oracle-se2", "MySQL" => "mysql", "PostgreSQL" => "postgres", _ => "sqlserver-ex"
    };

    private static string Network(HashSet<string> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            module "vpc" {
              source  = "terraform-aws-modules/vpc/aws"
              version = "~> 5.0"

              name = local.name
              cidr = var.vpc_cidr
              azs  = slice(data.aws_availability_zones.available.names, 0, 2)

              private_subnets = [cidrsubnet(var.vpc_cidr, 4, 0), cidrsubnet(var.vpc_cidr, 4, 1)]
              public_subnets  = [cidrsubnet(var.vpc_cidr, 4, 8), cidrsubnet(var.vpc_cidr, 4, 9)]

              enable_nat_gateway   = true
              single_nat_gateway   = true # um NAT para começar; dois para alta disponibilidade (custo fixo por NAT)
              enable_dns_hostnames = true
            }

            # Endpoints evitam que o tráfego para serviços AWS passe pelo NAT Gateway (custo por GB).
            resource "aws_vpc_endpoint" "s3" {
              vpc_id            = module.vpc.vpc_id
              service_name      = "com.amazonaws.${var.aws_region}.s3"
              vpc_endpoint_type = "Gateway"
              route_table_ids   = module.vpc.private_route_table_ids
            }

            resource "aws_security_group" "endpoints" {
              name        = "${local.name}-vpce"
              description = "Interface endpoints"
              vpc_id      = module.vpc.vpc_id
              ingress {
                from_port   = 443
                to_port     = 443
                protocol    = "tcp"
                cidr_blocks = [var.vpc_cidr]
              }
              egress {
                from_port   = 0
                to_port     = 0
                protocol    = "-1"
                cidr_blocks = ["0.0.0.0/0"]
              }
            }

            resource "aws_vpc_endpoint" "interfaces" {
              for_each            = toset(["ecr.api", "ecr.dkr", "logs", "secretsmanager", "ssm"])
              vpc_id              = module.vpc.vpc_id
              service_name        = "com.amazonaws.${var.aws_region}.${each.key}"
              vpc_endpoint_type   = "Interface"
              subnet_ids          = module.vpc.private_subnets
              security_group_ids  = [aws_security_group.endpoints.id]
              private_dns_enabled = true
            }
            """);
        if (components.Contains("vpn"))
            sb.AppendLine("""

            # Conectividade híbrida: a aplicação depende de hosts on-premises. Preencha com o gateway do datacenter e as rotas internas.
            # resource "aws_customer_gateway" "onprem" { bgp_asn = 65000  ip_address = "203.0.113.10"  type = "ipsec.1" }
            # resource "aws_vpn_gateway" "vgw" { vpc_id = module.vpc.vpc_id }
            # resource "aws_vpn_connection" "onprem" { vpn_gateway_id = aws_vpn_gateway.vgw.id  customer_gateway_id = aws_customer_gateway.onprem.id  type = "ipsec.1"  static_routes_only = true }
            # resource "aws_vpn_connection_route" "onprem" { destination_cidr_block = "10.0.0.0/8"  vpn_connection_id = aws_vpn_connection.onprem.id }
            # DNS interno: Route 53 Resolver outbound endpoint + regra de encaminhamento para o domínio corporativo.
            """);
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Iam(HashSet<string> components, List<Deployable> deployables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            # Execution role: puxa a imagem do ECR, grava logs e lê segredos para injetar como variáveis de ambiente.
            data "aws_iam_policy_document" "ecs_assume" {
              statement {
                actions = ["sts:AssumeRole"]
                principals {
                  type        = "Service"
                  identifiers = ["ecs-tasks.amazonaws.com"]
                }
              }
            }

            resource "aws_iam_role" "execution" {
              name               = "${local.name}-ecs-execution"
              assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
            }

            resource "aws_iam_role_policy_attachment" "execution_managed" {
              role       = aws_iam_role.execution.name
              policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy"
            }

            resource "aws_iam_role_policy" "execution_secrets" {
              name = "secrets"
              role = aws_iam_role.execution.id
              policy = jsonencode({
                Version = "2012-10-17"
                Statement = [{
                  Effect   = "Allow"
                  Action   = ["secretsmanager:GetSecretValue", "ssm:GetParameters", "kms:Decrypt"]
                  Resource = "*"
                }]
              })
            }

            # Task role: o que o código da aplicação pode fazer (sem access keys no código).
            resource "aws_iam_role" "task" {
              name               = "${local.name}-ecs-task"
              assume_role_policy = data.aws_iam_policy_document.ecs_assume.json
            }
            """);
        var statements = new List<string>();
        if (components.Contains("s3"))
            statements.Add("""
                    {
                      Effect   = "Allow"
                      Action   = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:ListBucket"]
                      Resource = [aws_s3_bucket.files.arn, "${aws_s3_bucket.files.arn}/*"]
                    }
                """);
        if (components.Contains("sqs") || components.Contains("s3-events"))
            statements.Add("""
                    {
                      Effect   = "Allow"
                      Action   = ["sqs:SendMessage", "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:GetQueueAttributes", "sqs:ChangeMessageVisibility"]
                      Resource = "arn:aws:sqs:${var.aws_region}:${data.aws_caller_identity.current.account_id}:${local.name}-*"
                    }
                """);
        if (components.Contains("ses") || components.Contains("ses-inbound"))
            statements.Add("""
                    {
                      Effect   = "Allow"
                      Action   = ["ses:SendEmail", "ses:SendRawEmail"]
                      Resource = "*"
                    }
                """);
        if (components.Contains("ssm"))
            statements.Add("""
                    {
                      Effect   = "Allow"
                      Action   = ["ssm:GetParameter", "ssm:GetParameters", "ssm:GetParametersByPath", "ssm:PutParameter"]
                      Resource = "arn:aws:ssm:${var.aws_region}:${data.aws_caller_identity.current.account_id}:parameter/${var.app_name}/*"
                    }
                """);
        statements.Add("""
                {
                  Effect   = "Allow"
                  Action   = ["secretsmanager:GetSecretValue"]
                  Resource = "arn:aws:secretsmanager:${var.aws_region}:${data.aws_caller_identity.current.account_id}:secret:${var.app_name}/*"
                }
            """);
        sb.AppendLine();
        sb.AppendLine("resource \"aws_iam_role_policy\" \"task\" {");
        sb.AppendLine("  name = \"app\"");
        sb.AppendLine("  role = aws_iam_role.task.id");
        sb.AppendLine("  policy = jsonencode({");
        sb.AppendLine("    Version = \"2012-10-17\"");
        sb.AppendLine("    Statement = [");
        sb.AppendLine(string.Join(",\n", statements.Select(s => string.Join("\n", s.TrimEnd('\n').Split('\n').Select(l => "  " + l)))));
        sb.AppendLine("    ]");
        sb.AppendLine("  })");
        sb.AppendLine("}");
        if (deployables.Any(d => d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask))
            sb.AppendLine("""

                # EventBridge Scheduler precisa de uma role para chamar ecs:RunTask e passar as roles da task.
                resource "aws_iam_role" "scheduler" {
                  name = "${local.name}-scheduler"
                  assume_role_policy = jsonencode({
                    Version = "2012-10-17"
                    Statement = [{ Effect = "Allow", Action = "sts:AssumeRole", Principal = { Service = "scheduler.amazonaws.com" } }]
                  })
                }

                resource "aws_iam_role_policy" "scheduler" {
                  name = "run-task"
                  role = aws_iam_role.scheduler.id
                  policy = jsonencode({
                    Version = "2012-10-17"
                    Statement = [
                      { Effect = "Allow", Action = ["ecs:RunTask"], Resource = "*" },
                      { Effect = "Allow", Action = ["iam:PassRole"], Resource = [aws_iam_role.execution.arn, aws_iam_role.task.arn] }
                    ]
                  })
                }
                """);
        if (deployables.Any(d => d.Result.Hosting!.Primary == AwsHosting.Lambda))
            sb.AppendLine("""

                resource "aws_iam_role" "lambda" {
                  name = "${local.name}-lambda"
                  assume_role_policy = jsonencode({
                    Version = "2012-10-17"
                    Statement = [{ Effect = "Allow", Action = "sts:AssumeRole", Principal = { Service = "lambda.amazonaws.com" } }]
                  })
                }

                resource "aws_iam_role_policy_attachment" "lambda_basic" {
                  role       = aws_iam_role.lambda.name
                  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
                }

                # A função usa as mesmas permissões de aplicação da task role (S3, SQS, SES, segredos).
                resource "aws_iam_role_policy" "lambda_app" {
                  name   = "app"
                  role   = aws_iam_role.lambda.id
                  policy = aws_iam_role_policy.task.policy
                }
                """);
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Ecs(string app, List<Deployable> webs, List<Deployable> scheduled, List<Deployable> workers, List<Deployable> windows, SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            resource "aws_ecs_cluster" "main" {
              name = local.name
              setting {
                name  = "containerInsights"
                value = "enabled"
              }
            }

            resource "aws_ecs_cluster_capacity_providers" "main" {
              cluster_name       = aws_ecs_cluster.main.name
              capacity_providers = ["FARGATE", "FARGATE_SPOT"]
              default_capacity_provider_strategy {
                capacity_provider = "FARGATE"
                weight            = 1
              }
            }

            resource "aws_security_group" "tasks" {
              name        = "${local.name}-tasks"
              description = "ECS tasks"
              vpc_id      = module.vpc.vpc_id
              egress {
                from_port   = 0
                to_port     = 0
                protocol    = "-1"
                cidr_blocks = ["0.0.0.0/0"]
              }
            }
            """);
        var all = webs.Concat(scheduled).Concat(workers).Concat(windows).ToList();
        foreach (var d in all)
        {
            var isWeb = d.Result.Project.Kind == ProjectKind.Web;
            var isWindows = d.Result.Hosting!.Primary == AwsHosting.EcsWindows;
            var secrets = SecretsFor(d.Result, result);
            sb.AppendLine();
            sb.AppendLine($"# ---------------------------------------------------------------- {d.Result.Project.Name} ({d.Result.Hosting.Primary.Display()})");
            sb.AppendLine($"resource \"aws_ecr_repository\" \"{d.Id}\" {{");
            sb.AppendLine($"  name                 = \"{app}/{d.Slug}\"");
            sb.AppendLine("  image_tag_mutability = \"MUTABLE\"");
            sb.AppendLine("  image_scanning_configuration {");
            sb.AppendLine("    scan_on_push = true");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_cloudwatch_log_group\" \"{d.Id}\" {{");
            sb.AppendLine($"  name              = \"/ecs/${{local.name}}/{d.Slug}\"");
            sb.AppendLine("  retention_in_days = 30");
            sb.AppendLine("}");
            foreach (var secret in secrets)
            {
                sb.AppendLine();
                sb.AppendLine($"data \"aws_secretsmanager_secret\" \"{d.Id}_{Id(secret.EnvironmentVariable)}\" {{");
                sb.AppendLine($"  name = \"{secret.SecretName}\" # crie antes com _secrets/{d.Result.Project.Name}/create-secrets.sh");
                sb.AppendLine("}");
            }
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_ecs_task_definition\" \"{d.Id}\" {{");
            sb.AppendLine($"  family                   = \"${{local.name}}-{d.Slug}\"");
            sb.AppendLine("  requires_compatibilities = [\"FARGATE\"]");
            sb.AppendLine("  network_mode             = \"awsvpc\"");
            sb.AppendLine($"  cpu                      = \"{(isWindows ? 1024 : 512)}\"");
            sb.AppendLine($"  memory                   = \"{(isWindows ? 2048 : 1024)}\"");
            sb.AppendLine("  execution_role_arn       = aws_iam_role.execution.arn");
            sb.AppendLine("  task_role_arn            = aws_iam_role.task.arn");
            sb.AppendLine("  runtime_platform {");
            sb.AppendLine($"    operating_system_family = \"{(isWindows ? "WINDOWS_SERVER_2022_CORE" : "LINUX")}\"");
            sb.AppendLine("    cpu_architecture        = \"X86_64\"");
            sb.AppendLine("  }");
            sb.AppendLine("  container_definitions = jsonencode([{");
            sb.AppendLine($"    name      = \"{d.Slug}\"");
            sb.AppendLine($"    image     = \"${{aws_ecr_repository.{d.Id}.repository_url}}:${{var.image_tag}}\"");
            sb.AppendLine("    essential = true");
            if (isWeb) sb.AppendLine("    portMappings = [{ containerPort = 8080, protocol = \"tcp\" }]");
            var hasSettings = result.Projects.Any(p => p.SettingsWithDependencies.Any(x => x.Kind != SettingKind.Secret));
            sb.AppendLine(hasSettings ? "    environment = concat([" : "    environment = [");
            sb.AppendLine($"      {{ name = \"{(isWeb ? "ASPNETCORE_ENVIRONMENT" : "DOTNET_ENVIRONMENT")}\", value = \"Production\" }},");
            sb.AppendLine("      { name = \"AWS_REGION\", value = var.aws_region }");
            sb.AppendLine(hasSettings ? "    ], [for k, v in var.app_settings : { name = k, value = v }])" : "    ]");
            sb.AppendLine("    secrets = [");
            sb.AppendLine(string.Join(",\n", secrets.Select(s => $"      {{ name = \"{s.EnvironmentVariable}\", valueFrom = data.aws_secretsmanager_secret.{d.Id}_{Id(s.EnvironmentVariable)}.arn }}")));
            sb.AppendLine("    ]");
            sb.AppendLine("    logConfiguration = {");
            sb.AppendLine("      logDriver = \"awslogs\"");
            sb.AppendLine("      options = {");
            sb.AppendLine($"        awslogs-group         = aws_cloudwatch_log_group.{d.Id}.name");
            sb.AppendLine("        awslogs-region        = var.aws_region");
            sb.AppendLine("        awslogs-stream-prefix = \"ecs\"");
            sb.AppendLine("      }");
            sb.AppendLine("    }");
            if (isWeb)
            {
                sb.AppendLine("    healthCheck = {");
                sb.AppendLine("      command     = [\"CMD-SHELL\", \"curl -fsS http://localhost:8080/health || exit 1\"]");
                sb.AppendLine("      interval    = 30");
                sb.AppendLine("      timeout     = 5");
                sb.AppendLine("      retries     = 3");
                sb.AppendLine("      startPeriod = 30");
                sb.AppendLine("    }");
            }
            sb.AppendLine("  }])");
            sb.AppendLine("}");

            if (isWeb)
            {
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_ecs_service\" \"{d.Id}\" {{");
                sb.AppendLine($"  name            = \"{d.Slug}\"");
                sb.AppendLine("  cluster         = aws_ecs_cluster.main.id");
                sb.AppendLine($"  task_definition = aws_ecs_task_definition.{d.Id}.arn");
                sb.AppendLine("  desired_count   = var.web_desired_count");
                sb.AppendLine("  launch_type     = \"FARGATE\"");
                sb.AppendLine("  network_configuration {");
                sb.AppendLine("    subnets         = module.vpc.private_subnets");
                sb.AppendLine("    security_groups = [aws_security_group.tasks.id]");
                sb.AppendLine("  }");
                sb.AppendLine("  load_balancer {");
                sb.AppendLine($"    target_group_arn = aws_lb_target_group.{d.Id}.arn");
                sb.AppendLine($"    container_name   = \"{d.Slug}\"");
                sb.AppendLine("    container_port   = 8080");
                sb.AppendLine("  }");
                sb.AppendLine("  deployment_circuit_breaker {");
                sb.AppendLine("    enable   = true");
                sb.AppendLine("    rollback = true");
                sb.AppendLine("  }");
                sb.AppendLine("  health_check_grace_period_seconds = 60");
                sb.AppendLine("  depends_on = [aws_lb_listener.http]");
                sb.AppendLine("}");
            }
            else if (d.Result.Hosting.Primary == AwsHosting.EcsScheduledTask)
            {
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_scheduler_schedule\" \"{d.Id}\" {{");
                sb.AppendLine($"  name                = \"${{local.name}}-{d.Slug}\"");
                sb.AppendLine($"  schedule_expression = var.schedules[\"{d.Id}\"]");
                sb.AppendLine("  flexible_time_window {");
                sb.AppendLine("    mode = \"OFF\"");
                sb.AppendLine("  }");
                sb.AppendLine("  target {");
                sb.AppendLine("    arn      = aws_ecs_cluster.main.arn");
                sb.AppendLine("    role_arn = aws_iam_role.scheduler.arn");
                sb.AppendLine("    ecs_parameters {");
                sb.AppendLine($"      task_definition_arn = aws_ecs_task_definition.{d.Id}.arn");
                sb.AppendLine("      launch_type         = \"FARGATE\"");
                sb.AppendLine("      network_configuration {");
                sb.AppendLine("        subnets         = module.vpc.private_subnets");
                sb.AppendLine("        security_groups = [aws_security_group.tasks.id]");
                sb.AppendLine("      }");
                sb.AppendLine("    }");
                sb.AppendLine("    retry_policy {");
                sb.AppendLine("      maximum_retry_attempts = 2");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
                sb.AppendLine("}");
            }
            else // worker or windows non-web
            {
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_ecs_service\" \"{d.Id}\" {{");
                sb.AppendLine($"  name            = \"{d.Slug}\"");
                sb.AppendLine("  cluster         = aws_ecs_cluster.main.id");
                sb.AppendLine($"  task_definition = aws_ecs_task_definition.{d.Id}.arn");
                sb.AppendLine("  desired_count   = 1");
                sb.AppendLine("  launch_type     = \"FARGATE\"");
                sb.AppendLine("  network_configuration {");
                sb.AppendLine("    subnets         = module.vpc.private_subnets");
                sb.AppendLine("    security_groups = [aws_security_group.tasks.id]");
                sb.AppendLine("  }");
                sb.AppendLine("}");
                if (d.Result.Hosting.Primary == AwsHosting.EcsFargateWorker)
                {
                    sb.AppendLine();
                    sb.AppendLine($"resource \"aws_appautoscaling_target\" \"{d.Id}\" {{");
                    sb.AppendLine("  max_capacity       = 4");
                    sb.AppendLine("  min_capacity       = 1");
                    sb.AppendLine($"  resource_id        = \"service/${{aws_ecs_cluster.main.name}}/${{aws_ecs_service.{d.Id}.name}}\"");
                    sb.AppendLine("  scalable_dimension = \"ecs:service:DesiredCount\"");
                    sb.AppendLine("  service_namespace  = \"ecs\"");
                    sb.AppendLine("}");
                    sb.AppendLine();
                    sb.AppendLine($"resource \"aws_appautoscaling_policy\" \"{d.Id}_queue\" {{");
                    sb.AppendLine($"  name               = \"${{local.name}}-{d.Slug}-queue-depth\"");
                    sb.AppendLine("  policy_type        = \"TargetTrackingScaling\"");
                    sb.AppendLine($"  resource_id        = aws_appautoscaling_target.{d.Id}.resource_id");
                    sb.AppendLine($"  scalable_dimension = aws_appautoscaling_target.{d.Id}.scalable_dimension");
                    sb.AppendLine($"  service_namespace  = aws_appautoscaling_target.{d.Id}.service_namespace");
                    sb.AppendLine("  target_tracking_scaling_policy_configuration {");
                    sb.AppendLine("    target_value = 10 # mensagens visíveis por task");
                    sb.AppendLine("    customized_metric_specification {");
                    sb.AppendLine("      metric_name = \"ApproximateNumberOfMessagesVisible\"");
                    sb.AppendLine("      namespace   = \"AWS/SQS\"");
                    sb.AppendLine("      statistic   = \"Average\"");
                    sb.AppendLine("      dimensions {");
                    sb.AppendLine("        name  = \"QueueName\"");
                    sb.AppendLine($"        value = aws_sqs_queue.{d.Id}.name");
                    sb.AppendLine("      }");
                    sb.AppendLine("    }");
                    sb.AppendLine("  }");
                    sb.AppendLine("}");
                }
            }
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Alb(List<Deployable> webs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            resource "aws_security_group" "alb" {
              name        = "${local.name}-alb"
              description = "Application Load Balancer"
              vpc_id      = module.vpc.vpc_id
              ingress {
                from_port   = 80
                to_port     = 80
                protocol    = "tcp"
                cidr_blocks = ["0.0.0.0/0"]
              }
              ingress {
                from_port   = 443
                to_port     = 443
                protocol    = "tcp"
                cidr_blocks = ["0.0.0.0/0"]
              }
              egress {
                from_port   = 0
                to_port     = 0
                protocol    = "-1"
                cidr_blocks = ["0.0.0.0/0"]
              }
            }

            resource "aws_security_group_rule" "tasks_from_alb" {
              type                     = "ingress"
              from_port                = 8080
              to_port                  = 8080
              protocol                 = "tcp"
              security_group_id        = aws_security_group.tasks.id
              source_security_group_id = aws_security_group.alb.id
            }

            resource "aws_lb" "main" {
              name               = substr(local.name, 0, 32)
              load_balancer_type = "application"
              subnets            = module.vpc.public_subnets
              security_groups    = [aws_security_group.alb.id]
              idle_timeout       = 60 # aumente se houver uploads/requisições longas (item MOD-ARCH-LONG-REQUESTS)
            }

            # HTTP: redireciona para HTTPS quando há certificado; senão serve direto (apenas testes).
            resource "aws_lb_listener" "http" {
              load_balancer_arn = aws_lb.main.arn
              port              = 80
              protocol          = "HTTP"
              dynamic "default_action" {
                for_each = var.certificate_arn == "" ? [1] : []
                content {
                  type = "fixed-response"
                  fixed_response {
                    content_type = "text/plain"
                    message_body = "not found"
                    status_code  = "404"
                  }
                }
              }
              dynamic "default_action" {
                for_each = var.certificate_arn == "" ? [] : [1]
                content {
                  type = "redirect"
                  redirect {
                    port        = "443"
                    protocol    = "HTTPS"
                    status_code = "HTTP_301"
                  }
                }
              }
            }

            resource "aws_lb_listener" "https" {
              count             = var.certificate_arn == "" ? 0 : 1
              load_balancer_arn = aws_lb.main.arn
              port              = 443
              protocol          = "HTTPS"
              ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
              certificate_arn   = var.certificate_arn
              default_action {
                type = "fixed-response"
                fixed_response {
                  content_type = "text/plain"
                  message_body = "not found"
                  status_code  = "404"
                }
              }
            }

            locals {
              listener_arn = var.certificate_arn == "" ? aws_lb_listener.http.arn : aws_lb_listener.https[0].arn
            }
            """);
        var priority = 10;
        foreach (var w in webs)
        {
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_lb_target_group\" \"{w.Id}\" {{");
            sb.AppendLine($"  name        = substr(\"${{local.name}}-{w.Slug}\", 0, 32)");
            sb.AppendLine("  port        = 8080");
            sb.AppendLine("  protocol    = \"HTTP\"");
            sb.AppendLine("  target_type = \"ip\"");
            sb.AppendLine("  vpc_id      = module.vpc.vpc_id");
            sb.AppendLine("  deregistration_delay = 30");
            sb.AppendLine("  health_check {");
            sb.AppendLine("    path                = \"/health\"");
            sb.AppendLine("    matcher             = \"200\"");
            sb.AppendLine("    interval            = 30");
            sb.AppendLine("    healthy_threshold   = 2");
            sb.AppendLine("    unhealthy_threshold = 3");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_lb_listener_rule\" \"{w.Id}\" {{");
            sb.AppendLine("  listener_arn = local.listener_arn");
            sb.AppendLine($"  priority     = {priority}");
            sb.AppendLine("  action {");
            sb.AppendLine("    type             = \"forward\"");
            sb.AppendLine($"    target_group_arn = aws_lb_target_group.{w.Id}.arn");
            sb.AppendLine("  }");
            sb.AppendLine("  condition {");
            sb.AppendLine("    host_header {");
            sb.AppendLine($"      values = [var.web_hosts[\"{w.Id}\"]]");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            priority += 10;
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Lambda(List<Deployable> lambdas, HashSet<string> components)
    {
        var sb = new StringBuilder();
        foreach (var l in lambdas)
        {
            var fileDriven = l.Profile.HasAny(Signal.FileWatcher, Signal.Ftp, Signal.MailboxReading) || l.Profile.HasAny(Signal.FileSystemWrites, Signal.UncPaths, Signal.WindowsPaths) && l.Profile.Has(Signal.SpreadsheetFiles);
            var queueDriven = l.Profile.HasAny(Signal.Msmq, Signal.RabbitMq, Signal.MessageBusFramework, Signal.Kafka, Signal.AzureServiceBus);
            if (!fileDriven && !queueDriven) fileDriven = true;
            var handler = fileDriven ? "FunctionHandler" : "QueueHandler";
            var ns = string.IsNullOrWhiteSpace(l.Result.Project.RootNamespace) ? l.Result.Project.Name : l.Result.Project.RootNamespace;
            var secrets = SecretsFor(l.Result, null);
            sb.AppendLine($"# ---------------------------------------------------------------- {l.Result.Project.Name} (AWS Lambda)");
            sb.AppendLine($"resource \"aws_cloudwatch_log_group\" \"{l.Id}\" {{");
            sb.AppendLine($"  name              = \"/aws/lambda/${{local.name}}-{l.Slug}\"");
            sb.AppendLine("  retention_in_days = 30");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_lambda_function\" \"{l.Id}\" {{");
            sb.AppendLine($"  function_name    = \"${{local.name}}-{l.Slug}\"");
            sb.AppendLine("  role             = aws_iam_role.lambda.arn");
            sb.AppendLine("  runtime          = var.lambda_runtime");
            sb.AppendLine($"  handler          = \"{l.Result.Project.AssemblyName}::{ns}.Function::{handler}\"");
            sb.AppendLine($"  filename         = var.lambda_packages[\"{l.Id}\"]");
            sb.AppendLine($"  source_code_hash = filebase64sha256(var.lambda_packages[\"{l.Id}\"])");
            sb.AppendLine("  architectures    = [\"arm64\"]");
            sb.AppendLine("  memory_size      = 512");
            sb.AppendLine("  timeout          = 300");
            sb.AppendLine("  vpc_config {");
            sb.AppendLine("    subnet_ids         = module.vpc.private_subnets");
            sb.AppendLine("    security_group_ids = [aws_security_group.tasks.id]");
            sb.AppendLine("  }");
            var lambdaSettings = l.Result.SettingsWithDependencies.Any(x => x.Kind != SettingKind.Secret);
            sb.AppendLine("  environment {");
            sb.AppendLine(lambdaSettings ? "    variables = merge({" : "    variables = {");
            sb.AppendLine("      DOTNET_ENVIRONMENT = \"Production\"");
            sb.AppendLine("      # Segredos: leia em tempo de execução pelo nome (AWSSDK.SecretsManager) ou carregue no IConfiguration com Amazon.Extensions.Configuration.SystemsManager.");
            foreach (var s in secrets) sb.AppendLine($"      SECRET_{Id(s.EnvironmentVariable).ToUpperInvariant()} = \"{s.SecretName}\"");
            sb.AppendLine(lambdaSettings ? "    }, var.app_settings)" : "    }");
            sb.AppendLine("  }");
            sb.AppendLine($"  depends_on = [aws_cloudwatch_log_group.{l.Id}]");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_sqs_queue\" \"{l.Id}_dlq\" {{");
            sb.AppendLine($"  name = \"${{local.name}}-{l.Slug}-dlq\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_sqs_queue\" \"{l.Id}\" {{");
            sb.AppendLine($"  name                       = \"${{local.name}}-{l.Slug}\"");
            sb.AppendLine("  visibility_timeout_seconds = 360 # >= timeout da função");
            sb.AppendLine("  redrive_policy = jsonencode({");
            sb.AppendLine($"    deadLetterTargetArn = aws_sqs_queue.{l.Id}_dlq.arn");
            sb.AppendLine("    maxReceiveCount     = 3");
            sb.AppendLine("  })");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_lambda_event_source_mapping\" \"{l.Id}\" {{");
            sb.AppendLine($"  event_source_arn = aws_sqs_queue.{l.Id}.arn");
            sb.AppendLine($"  function_name    = aws_lambda_function.{l.Id}.arn");
            sb.AppendLine("  batch_size       = 1");
            sb.AppendLine("}");
            if (fileDriven && components.Contains("s3"))
            {
                sb.AppendLine();
                sb.AppendLine($"# Arquivos: cada objeto novo no prefixo de entrada vira uma mensagem na fila da função.");
                sb.AppendLine($"resource \"aws_sqs_queue_policy\" \"{l.Id}_s3\" {{");
                sb.AppendLine($"  queue_url = aws_sqs_queue.{l.Id}.id");
                sb.AppendLine("  policy = jsonencode({");
                sb.AppendLine("    Version = \"2012-10-17\"");
                sb.AppendLine("    Statement = [{");
                sb.AppendLine("      Effect    = \"Allow\"");
                sb.AppendLine("      Principal = { Service = \"s3.amazonaws.com\" }");
                sb.AppendLine("      Action    = \"sqs:SendMessage\"");
                sb.AppendLine($"      Resource  = aws_sqs_queue.{l.Id}.arn");
                sb.AppendLine("      Condition = { ArnEquals = { \"aws:SourceArn\" = aws_s3_bucket.files.arn } }");
                sb.AppendLine("    }]");
                sb.AppendLine("  })");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_s3_bucket_notification\" \"{l.Id}\" {{");
                sb.AppendLine("  bucket = aws_s3_bucket.files.id");
                sb.AppendLine("  queue {");
                sb.AppendLine($"    queue_arn     = aws_sqs_queue.{l.Id}.arn");
                sb.AppendLine("    events        = [\"s3:ObjectCreated:*\"]");
                sb.AppendLine($"    filter_prefix = \"{l.Slug}/entrada/\"");
                sb.AppendLine("  }");
                sb.AppendLine($"  depends_on = [aws_sqs_queue_policy.{l.Id}_s3]");
                sb.AppendLine("}");
            }
            if (l.Profile.Has(Signal.MailboxReading))
            {
                sb.AppendLine();
                sb.AppendLine("# E-mail: regra de recebimento do SES grava a mensagem no bucket (prefixo email/) e o evento do S3 dispara a função.");
                sb.AppendLine($"resource \"aws_ses_receipt_rule_set\" \"{l.Id}\" {{");
                sb.AppendLine("  count         = var.enable_ses_inbound ? 1 : 0");
                sb.AppendLine($"  rule_set_name = \"${{local.name}}-{l.Slug}\"");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_ses_active_receipt_rule_set\" \"{l.Id}\" {{");
                sb.AppendLine("  count         = var.enable_ses_inbound ? 1 : 0");
                sb.AppendLine($"  rule_set_name = aws_ses_receipt_rule_set.{l.Id}[0].rule_set_name");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine($"resource \"aws_ses_receipt_rule\" \"{l.Id}\" {{");
                sb.AppendLine("  count         = var.enable_ses_inbound ? 1 : 0");
                sb.AppendLine($"  name          = \"{l.Slug}-para-s3\"");
                sb.AppendLine($"  rule_set_name = aws_ses_receipt_rule_set.{l.Id}[0].rule_set_name");
                sb.AppendLine("  recipients    = var.inbound_recipients");
                sb.AppendLine("  enabled       = true");
                sb.AppendLine("  scan_enabled  = true");
                sb.AppendLine("  s3_action {");
                sb.AppendLine("    bucket_name       = aws_s3_bucket.files.id");
                sb.AppendLine($"    object_key_prefix = \"{l.Slug}/entrada/email/\"");
                sb.AppendLine("    position          = 1");
                sb.AppendLine("  }");
                sb.AppendLine("}");
            }
            sb.AppendLine();
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Data(SolutionResult result, HashSet<string> components)
    {
        if (result.Databases.Count == 0 && !components.Keys().Any(k => k.StartsWith("rds"))) return "";
        var sb = new StringBuilder();
        var names = string.Join(", ", result.Databases.Select(d => d.Database ?? d.Name).Distinct().Take(5));
        sb.AppendLine($$"""
            # Banco(s) detectado(s): {{names}}. Migre os dados com restore nativo (.bak via S3) ou AWS DMS.
            resource "aws_db_subnet_group" "main" {
              name       = local.name
              subnet_ids = module.vpc.private_subnets
            }

            resource "aws_security_group" "db" {
              name        = "${local.name}-db"
              description = "RDS"
              vpc_id      = module.vpc.vpc_id
              ingress {
                from_port       = local.db_port
                to_port         = local.db_port
                protocol        = "tcp"
                security_groups = [aws_security_group.tasks.id]
              }
              egress {
                from_port   = 0
                to_port     = 0
                protocol    = "-1"
                cidr_blocks = ["0.0.0.0/0"]
              }
            }

            locals {
              db_port = startswith(var.db_engine, "sqlserver") ? 1433 : startswith(var.db_engine, "oracle") ? 1521 : var.db_engine == "mysql" ? 3306 : 5432
            }

            resource "aws_db_instance" "main" {
              identifier              = local.name
              engine                  = var.db_engine
              instance_class          = var.db_instance_class
              allocated_storage       = var.db_allocated_storage
              storage_type            = "gp3"
              storage_encrypted       = true
              db_subnet_group_name    = aws_db_subnet_group.main.name
              vpc_security_group_ids  = [aws_security_group.db.id]
              username                = var.db_username
              manage_master_user_password = true # senha gerenciada e rotacionada pelo Secrets Manager
              multi_az                = var.environment == "prod"
              backup_retention_period = 7
              deletion_protection     = var.environment == "prod"
              skip_final_snapshot     = var.environment != "prod"
              license_model           = startswith(var.db_engine, "sqlserver") || var.db_engine == "oracle-se2" ? "license-included" : null
              apply_immediately       = var.environment != "prod"
            }
            """);
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Storage(HashSet<string> components, List<Deployable> deployables, List<Deployable> consumers)
    {
        var sb = new StringBuilder();
        if (components.Contains("s3"))
            sb.AppendLine("""
                resource "aws_s3_bucket" "files" {
                  bucket = "${local.name}-${data.aws_caller_identity.current.account_id}-files"
                }

                resource "aws_s3_bucket_public_access_block" "files" {
                  bucket                  = aws_s3_bucket.files.id
                  block_public_acls       = true
                  block_public_policy     = true
                  ignore_public_acls      = true
                  restrict_public_buckets = true
                }

                resource "aws_s3_bucket_versioning" "files" {
                  bucket = aws_s3_bucket.files.id
                  versioning_configuration {
                    status = "Enabled"
                  }
                }

                resource "aws_s3_bucket_server_side_encryption_configuration" "files" {
                  bucket = aws_s3_bucket.files.id
                  rule {
                    apply_server_side_encryption_by_default {
                      sse_algorithm = "AES256"
                    }
                  }
                }

                resource "aws_s3_bucket_lifecycle_configuration" "files" {
                  bucket = aws_s3_bucket.files.id
                  rule {
                    id     = "processados-para-glacier"
                    status = "Enabled"
                    filter {
                      prefix = "processados/"
                    }
                    transition {
                      days          = 90
                      storage_class = "GLACIER_IR"
                    }
                  }
                }
                """);
        var workers = deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker).ToList();
        foreach (var w in workers)
        {
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_sqs_queue\" \"{w.Id}_dlq\" {{");
            sb.AppendLine($"  name = \"${{local.name}}-{w.Slug}-dlq\"");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_sqs_queue\" \"{w.Id}\" {{");
            sb.AppendLine($"  name                       = \"${{local.name}}-{w.Slug}\"");
            sb.AppendLine("  visibility_timeout_seconds = 300");
            sb.AppendLine("  redrive_policy = jsonencode({");
            sb.AppendLine($"    deadLetterTargetArn = aws_sqs_queue.{w.Id}_dlq.arn");
            sb.AppendLine("    maxReceiveCount     = 5");
            sb.AppendLine("  })");
            sb.AppendLine("}");
        }
        if (components.Contains("sqs") && workers.Count == 0 && consumers.Count == 0)
            sb.AppendLine("""

                # Fila genérica para desacoplar a web dos processamentos em segundo plano (recomendada pela arquitetura).
                resource "aws_sqs_queue" "jobs_dlq" {
                  name = "${local.name}-jobs-dlq"
                }

                resource "aws_sqs_queue" "jobs" {
                  name                       = "${local.name}-jobs"
                  visibility_timeout_seconds = 300
                  redrive_policy = jsonencode({
                    deadLetterTargetArn = aws_sqs_queue.jobs_dlq.arn
                    maxReceiveCount     = 5
                  })
                }
                """);
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Cache() => """
        # Sessão distribuída, cache compartilhado e backplane do SignalR (Microsoft.Extensions.Caching.StackExchangeRedis).
        resource "aws_security_group" "cache" {
          name        = "${local.name}-cache"
          description = "ElastiCache"
          vpc_id      = module.vpc.vpc_id
          ingress {
            from_port       = 6379
            to_port         = 6379
            protocol        = "tcp"
            security_groups = [aws_security_group.tasks.id]
          }
        }

        resource "aws_elasticache_serverless_cache" "main" {
          engine               = "valkey"
          name                 = local.name
          security_group_ids   = [aws_security_group.cache.id]
          subnet_ids           = module.vpc.private_subnets
          cache_usage_limits {
            data_storage {
              maximum = 5
              unit    = "GB"
            }
            ecpu_per_second {
              maximum = 5000
            }
          }
        }
        """.Replace("\r\n", "\n");

    private static string Observability(List<Deployable> webs, List<Deployable> containers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            resource "aws_sns_topic" "alerts" {
              name = "${local.name}-alerts"
            }

            resource "aws_sns_topic_subscription" "alerts_email" {
              count     = var.alert_email == "" ? 0 : 1
              topic_arn = aws_sns_topic.alerts.arn
              protocol  = "email"
              endpoint  = var.alert_email
            }
            """);
        if (webs.Count > 0)
            sb.AppendLine("""

                resource "aws_cloudwatch_metric_alarm" "alb_5xx" {
                  alarm_name          = "${local.name}-alb-5xx"
                  comparison_operator = "GreaterThanThreshold"
                  evaluation_periods  = 2
                  metric_name         = "HTTPCode_Target_5XX_Count"
                  namespace           = "AWS/ApplicationELB"
                  period              = 60
                  statistic           = "Sum"
                  threshold           = 10
                  treat_missing_data  = "notBreaching"
                  dimensions = {
                    LoadBalancer = aws_lb.main.arn_suffix
                  }
                  alarm_actions = [aws_sns_topic.alerts.arn]
                }
                """);
        foreach (var d in containers.Where(c => c.Result.Project.Kind == ProjectKind.Web || c.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker))
        {
            sb.AppendLine();
            sb.AppendLine($"resource \"aws_cloudwatch_metric_alarm\" \"{d.Id}_cpu\" {{");
            sb.AppendLine($"  alarm_name          = \"${{local.name}}-{d.Slug}-cpu\"");
            sb.AppendLine("  comparison_operator = \"GreaterThanThreshold\"");
            sb.AppendLine("  evaluation_periods  = 3");
            sb.AppendLine("  metric_name         = \"CPUUtilization\"");
            sb.AppendLine("  namespace           = \"AWS/ECS\"");
            sb.AppendLine("  period              = 300");
            sb.AppendLine("  statistic           = \"Average\"");
            sb.AppendLine("  threshold           = 80");
            sb.AppendLine("  dimensions = {");
            sb.AppendLine("    ClusterName = aws_ecs_cluster.main.name");
            sb.AppendLine($"    ServiceName = aws_ecs_service.{d.Id}.name");
            sb.AppendLine("  }");
            sb.AppendLine("  alarm_actions = [aws_sns_topic.alerts.arn]");
            sb.AppendLine("}");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Outputs(List<Deployable> webs, List<Deployable> containers, List<Deployable> lambdas, HashSet<string> components, SolutionResult result)
    {
        var sb = new StringBuilder();
        if (webs.Count > 0)
            sb.AppendLine("""
                output "alb_dns_name" {
                  description = "Aponte os hosts de var.web_hosts (CNAME/alias no Route 53) para este endereço."
                  value       = aws_lb.main.dns_name
                }
                """);
        if (containers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("output \"ecr_repositories\" {");
            sb.AppendLine("  value = {");
            foreach (var c in containers) sb.AppendLine($"    {c.Id} = aws_ecr_repository.{c.Id}.repository_url");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("output \"ecs_cluster\" {");
            sb.AppendLine("  value = aws_ecs_cluster.main.name");
            sb.AppendLine("}");
        }
        if (lambdas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("output \"lambda_functions\" {");
            sb.AppendLine("  value = {");
            foreach (var l in lambdas) sb.AppendLine($"    {l.Id} = aws_lambda_function.{l.Id}.function_name");
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }
        if (result.Databases.Count > 0)
            sb.AppendLine("""

                output "db_endpoint" {
                  value = aws_db_instance.main.address
                }

                output "db_master_secret_arn" {
                  description = "Segredo gerenciado pelo RDS com a senha do usuário master."
                  value       = try(aws_db_instance.main.master_user_secret[0].secret_arn, null)
                }
                """);
        if (components.Contains("s3")) sb.AppendLine("\noutput \"files_bucket\" {\n  value = aws_s3_bucket.files.id\n}");
        if (components.Contains("elasticache")) sb.AppendLine("\noutput \"cache_endpoint\" {\n  value = aws_elasticache_serverless_cache.main.endpoint\n}");
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string TfVarsExample(string app, List<Deployable> webs, SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Copie para terraform.tfvars e ajuste. Nada aqui é segredo.");
        sb.AppendLine("aws_region  = \"sa-east-1\"");
        sb.AppendLine("environment = \"hml\"");
        sb.AppendLine($"app_name    = \"{app}\"");
        sb.AppendLine("image_tag   = \"latest\"");
        sb.AppendLine("# certificate_arn = \"arn:aws:acm:sa-east-1:123456789012:certificate/...\"");
        sb.AppendLine("# alert_email     = \"time-plataforma@empresa.com.br\"");
        if (webs.Count > 0)
        {
            sb.AppendLine("web_hosts = {");
            foreach (var w in webs) sb.AppendLine($"  {w.Id} = \"{w.Slug}-hml.empresa.com.br\"");
            sb.AppendLine("}");
        }
        if (result.Databases.Count > 0) sb.AppendLine($"db_engine = \"{DefaultEngine(result)}\"");
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string Readme(SolutionResult result, List<Deployable> deployables, List<Deployable> lambdas, List<Deployable> ec2, List<ProjectResult> notGenerated, HashSet<string> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Infraestrutura de {result.SolutionName} na AWS");
        sb.AppendLine();
        sb.AppendLine("Gerado pelo Migrator a partir da arquitetura proposta (veja `_migration-report/migration-report.html`, seção \"Arquitetura alvo\"). É um ponto de partida revisável, não um ambiente de produção pronto: leia os comentários, ajuste tamanhos, domínios e políticas antes do `apply`.");
        sb.AppendLine();
        sb.AppendLine("## O que é criado");
        sb.AppendLine();
        foreach (var d in deployables) sb.AppendLine($"- **{d.Result.Project.Name}** → {d.Result.Hosting!.Primary.Display()}");
        foreach (var e in ec2) sb.AppendLine($"- **{e.Result.Project.Name}** → EC2 Windows: não gerado (crie com EC2 Image Builder + Auto Scaling; depende de IIS/Web Forms).");
        foreach (var n in notGenerated) sb.AppendLine($"- **{n.Project.Name}** → {n.Hosting!.Primary.Display()}: não gerado ({(n.Project.IsVisualBasic ? "projeto VB.NET não convertido; converta e rode o Migrator de novo" : "projeto sem saída migrada")}).");
        sb.AppendLine("- VPC com subnets privadas/públicas, NAT único e VPC endpoints (S3, ECR, Logs, Secrets Manager, SSM)" + (components.Contains("vpn") ? "; esqueleto comentado da VPN para os hosts on-premises" : ""));
        if (result.Databases.Count > 0) sb.AppendLine($"- RDS ({DefaultEngine(result)} por padrão) com senha master gerenciada pelo Secrets Manager");
        if (components.Contains("s3")) sb.AppendLine("- Bucket S3 privado, versionado e criptografado para arquivos (prefixos `<app>/entrada/` e `processados/`)");
        if (components.Contains("elasticache")) sb.AppendLine("- ElastiCache Serverless (Valkey) para sessão/cache");
        sb.AppendLine("- SNS + alarmes do CloudWatch (5xx no ALB, CPU dos serviços)");
        sb.AppendLine("- `.github/workflows/deploy.yml`: build, testes, imagens no ECR e deploy no ECS/Lambda a cada push na `main`");
        sb.AppendLine();
        sb.AppendLine("## Ordem de execução");
        sb.AppendLine();
        sb.AppendLine("1. **Segredos**: rode `_secrets/<projeto>/create-secrets.sh` para cada projeto (os `data \"aws_secretsmanager_secret\"` do Terraform exigem que existam). Depois rotacione as credenciais que estavam em texto claro.");
        sb.AppendLine("2. **Estado remoto**: configure o `backend \"s3\"` em `terraform/versions.tf`.");
        sb.AppendLine("3. `cd infra/terraform && cp terraform.tfvars.example terraform.tfvars` e ajuste região, ambiente, hosts e engine do banco.");
        sb.AppendLine("4. `terraform init && terraform plan && terraform apply`.");
        sb.AppendLine("5. **Imagens**: o primeiro `apply` cria os repositórios ECR; publique as imagens (workflow ou `docker build -f <proj>/Dockerfile .` + `docker push`) e rode `apply` de novo ou `aws ecs update-service --force-new-deployment`.");
        if (lambdas.Count > 0) sb.AppendLine("6. **Lambda**: `dotnet lambda package` em cada projeto Lambda gera o .zip apontado em `var.lambda_packages`; o `apply` publica a função. Alternativa: `dotnet lambda deploy-function` direto.");
        if (result.Databases.Count > 0) sb.AppendLine($"{(lambdas.Count > 0 ? 7 : 6)}. **Dados**: restaure o backup no RDS (`.bak` no S3 + `rds_restore_database`, ou AWS DMS) e crie o usuário da aplicação; atualize o segredo da connection string.");
        sb.AppendLine($"{(lambdas.Count > 0 ? 8 : 7)}. **DNS**: aponte os hosts de `var.web_hosts` para `alb_dns_name` (Route 53 alias) e informe `certificate_arn` (ACM) para HTTPS.");
        sb.AppendLine();
        sb.AppendLine("## Antes de produção");
        sb.AppendLine();
        sb.AppendLine("- Dois NAT Gateways (um por AZ) e `web_desired_count >= 2`.");
        sb.AppendLine("- AWS WAF no ALB para aplicações públicas; access logs do ALB em S3.");
        sb.AppendLine("- Política de retenção dos logs e budget/alertas de custo.");
        sb.AppendLine("- Revisar as políticas IAM geradas (`iam.tf`): estão restritas ao prefixo da aplicação, mas S3/SQS/SES podem ser apertados por recurso.");
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ------------------------------------------------------------------ CI/CD

    /// <summary>Shared with the CloudFormation generator: the ECS/Lambda deploy workflow only depends on names, not on the IaC tool.</summary>
    internal static string Workflow(string app, List<(ProjectResult Result, ApplicationProfile Profile)> containers, List<(ProjectResult Result, ApplicationProfile Profile)> lambdas, SolutionResult result) =>
        Workflow(app, containers.Select(c => new Deployable(c.Result, c.Profile, Slug(c.Result.Project.Name), Id(c.Result.Project.Name))).ToList(),
            lambdas.Select(l => new Deployable(l.Result, l.Profile, Slug(l.Result.Project.Name), Id(l.Result.Project.Name))).ToList(), result);

    private static string Workflow(string app, List<Deployable> containers, List<Deployable> lambdas, SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Gerado pelo Migrator: build, testes, imagens no ECR e deploy no ECS/Lambda para {result.SolutionName}.");
        sb.AppendLine("# Pré-requisitos: role IAM para GitHub OIDC em AWS_ROLE_ARN (secret), infraestrutura criada com infra/terraform.");
        sb.AppendLine("name: deploy");
        sb.AppendLine();
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main]");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine();
        sb.AppendLine("permissions:");
        sb.AppendLine("  id-token: write");
        sb.AppendLine("  contents: read");
        sb.AppendLine();
        sb.AppendLine("env:");
        sb.AppendLine("  AWS_REGION: sa-east-1");
        sb.AppendLine($"  APP_NAME: {app}");
        sb.AppendLine("  ENVIRONMENT: prod");
        sb.AppendLine("  DOTNET_VERSION: 10.0.x");
        sb.AppendLine();
        sb.AppendLine("jobs:");
        sb.AppendLine("  build-test:");
        sb.AppendLine("    runs-on: ubuntu-latest");
        sb.AppendLine("    steps:");
        sb.AppendLine("      - uses: actions/checkout@v4");
        sb.AppendLine("      - uses: actions/setup-dotnet@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          dotnet-version: ${{ env.DOTNET_VERSION }}");
        sb.AppendLine("      - run: dotnet restore");
        sb.AppendLine("      - run: dotnet build --no-restore -c Release");
        sb.AppendLine("      - run: dotnet test --no-build -c Release");
        if (containers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  containers:");
            sb.AppendLine("    needs: build-test");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      fail-fast: false");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        include:");
            foreach (var c in containers)
            {
                sb.AppendLine($"          - name: {c.Slug}");
                sb.AppendLine($"            dockerfile: {c.Result.RelativeDir.Replace('\\', '/')}/Dockerfile");
                sb.AppendLine($"            service: {c.Slug}");
                sb.AppendLine($"            kind: {(c.Result.Project.Kind == ProjectKind.Web || c.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker || c.Result.Hosting.Primary == AwsHosting.EcsWindows ? "service" : "task")}");
            }
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/checkout@v4");
            sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
            sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
            sb.AppendLine("      - id: ecr");
            sb.AppendLine("        uses: aws-actions/amazon-ecr-login@v2");
            sb.AppendLine("      - name: Build e push");
            sb.AppendLine("        run: |");
            sb.AppendLine("          IMAGE=${{ steps.ecr.outputs.registry }}/${{ env.APP_NAME }}/${{ matrix.name }}");
            sb.AppendLine("          docker build -f ${{ matrix.dockerfile }} -t $IMAGE:${{ github.sha }} -t $IMAGE:latest .");
            sb.AppendLine("          docker push $IMAGE --all-tags");
            sb.AppendLine("      - name: Deploy no ECS");
            sb.AppendLine("        if: matrix.kind == 'service'");
            sb.AppendLine("        run: |");
            sb.AppendLine("          aws ecs update-service --cluster ${{ env.APP_NAME }}-${{ env.ENVIRONMENT }} --service ${{ matrix.service }} --force-new-deployment");
            sb.AppendLine("          aws ecs wait services-stable --cluster ${{ env.APP_NAME }}-${{ env.ENVIRONMENT }} --services ${{ matrix.service }}");
            sb.AppendLine("      - name: Tarefa agendada atualizada");
            sb.AppendLine("        if: matrix.kind == 'task'");
            sb.AppendLine("        run: echo \"A próxima execução do EventBridge Scheduler usa a imagem :latest recém-publicada.\"");
        }
        if (lambdas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  lambdas:");
            sb.AppendLine("    needs: build-test");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        include:");
            foreach (var l in lambdas)
            {
                sb.AppendLine($"          - name: {l.Slug}");
                sb.AppendLine($"            project: {l.Result.OutputProjectPath!.Replace('\\', '/')}");
            }
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/checkout@v4");
            sb.AppendLine("      - uses: actions/setup-dotnet@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          dotnet-version: ${{ env.DOTNET_VERSION }}");
            sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
            sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
            sb.AppendLine("      - run: dotnet tool install -g Amazon.Lambda.Tools");
            sb.AppendLine("      - name: Package e deploy");
            sb.AppendLine("        run: |");
            sb.AppendLine("          cd $(dirname ${{ matrix.project }})");
            sb.AppendLine("          dotnet lambda deploy-function ${{ env.APP_NAME }}-${{ env.ENVIRONMENT }}-${{ matrix.name }} --region ${{ env.AWS_REGION }}");
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Secrets the project's appsettings lost to _secrets/ (recomputed from the extracted plan when available).</summary>
    private static List<ExtractedSecret> SecretsFor(ProjectResult project, SolutionResult? _) =>
        project.Secrets?.Secrets.Where(s => s.Environment is null or "Production").DistinctBy(s => s.EnvironmentVariable).ToList() ?? [];

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');

    private static string Id(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');

    private static IEnumerable<string> Keys(this HashSet<string> set) => set;
}
