using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>Terraform + workflow generation, checked on the migrated sample (dry run output is enough: the generator only needs the result).</summary>
public sealed class IacGenerationTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-iac-gen-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string SampleSolution()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "LegacyShop", "LegacyShop.sln");
    }

    [Fact]
    public async Task Migrate_writes_terraform_and_workflow_from_the_architecture()
    {
        var output = Path.Combine(_work, "out");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = false });
        string Read(string relative) => File.ReadAllText(Path.Combine(output, relative));

        Assert.Contains(result.GlobalItems, i => i.RuleId == "AWS-INFRA" && i.AutoMigrated);
        foreach (var file in new[] { "versions.tf", "variables.tf", "network.tf", "iam.tf", "ecs.tf", "alb.tf", "lambda.tf", "data.tf", "storage.tf", "cache.tf", "observability.tf", "outputs.tf", "terraform.tfvars.example" })
            Assert.True(File.Exists(Path.Combine(output, "infra", "terraform", file)), file);
        Assert.True(File.Exists(Path.Combine(output, "infra", "README.md")));
        Assert.True(File.Exists(Path.Combine(output, ".github", "workflows", "deploy.yml")));

        var ecs = Read("infra/terraform/ecs.tf");
        Assert.Contains("resource \"aws_ecs_service\" \"legacyshop_web\"", ecs);                 // web → service behind the ALB
        Assert.Contains("resource \"aws_scheduler_schedule\" \"legacyshop_worker\"", ecs);       // worker → EventBridge scheduled task
        Assert.DoesNotContain("legacyshop_relatorios", ecs);                                     // VB project is not converted: no task definition
        Assert.Contains("LegacyShop.Relatorios", Read("infra/README.md"));                       // ...but the README says why it is missing
        Assert.Contains("operating_system_family = \"LINUX\"", ecs);
        Assert.Contains("awslogs-group", ecs);
        Assert.Contains("data \"aws_secretsmanager_secret\" \"legacyshop_web_connectionstrings__relatoriosconnection\"", ecs);
        Assert.Contains("valueFrom = data.aws_secretsmanager_secret.legacyshop_web_connectionstrings__relatoriosconnection.arn", ecs);
        Assert.DoesNotContain("Senha@123", ecs);                                                 // never a credential value

        var lambda = Read("infra/terraform/lambda.tf");
        Assert.Contains("resource \"aws_lambda_function\" \"legacyshop_importador\"", lambda);
        Assert.Contains("LegacyShop.Importador::LegacyShop.Importador.Function::FunctionHandler", lambda);
        Assert.Contains("resource \"aws_s3_bucket_notification\"", lambda);                        // files trigger
        Assert.Contains("resource \"aws_ses_receipt_rule\"", lambda);                              // mailbox trigger (opt-in)
        Assert.Contains("aws_lambda_event_source_mapping", lambda);

        var alb = Read("infra/terraform/alb.tf");
        Assert.Contains("path                = \"/health\"", alb);
        Assert.Contains("var.web_hosts[\"legacyshop_web\"]", alb);

        var data = Read("infra/terraform/data.tf");
        Assert.Contains("resource \"aws_db_instance\" \"main\"", data);
        Assert.Contains("manage_master_user_password = true", data);
        Assert.Contains("default     = \"sqlserver-ex\"", Read("infra/terraform/variables.tf"));

        Assert.Contains("aws_elasticache_serverless_cache", Read("infra/terraform/cache.tf"));
        Assert.Contains("resource \"aws_s3_bucket\" \"files\"", Read("infra/terraform/storage.tf"));
        Assert.Contains("aws_vpn_connection", Read("infra/terraform/network.tf"));                 // on-prem hosts → commented VPN skeleton

        var workflow = Read(".github/workflows/deploy.yml");
        Assert.Contains("aws-actions/amazon-ecr-login@v2", workflow);
        Assert.Contains("dockerfile: LegacyShop.Web/Dockerfile", workflow);
        Assert.Contains("dotnet lambda deploy-function", workflow);
        Assert.Contains("role-to-assume: ${{ secrets.AWS_ROLE_ARN }}", workflow);
    }

    [Fact]
    public async Task No_infra_flag_and_cloud_none_skip_generation()
    {
        var output = Path.Combine(_work, "out2");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = false, GenerateInfrastructure = false });
        Assert.False(Directory.Exists(Path.Combine(output, "infra")));
        Assert.DoesNotContain(result.GlobalItems, i => i.RuleId == "AWS-INFRA");

        var output2 = Path.Combine(_work, "out3");
        await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = SampleSolution(), OutputDir = output2, Offline = true, VerifyBuild = false, Cloud = CloudTarget.None });
        Assert.False(Directory.Exists(Path.Combine(output2, "infra")));
    }
}
