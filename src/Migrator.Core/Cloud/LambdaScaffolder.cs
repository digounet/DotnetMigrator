using System.Text;
using System.Text.Json.Nodes;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// For projects recommended as AWS Lambda: a Function.cs with one handler per detected trigger (S3 for files/e-mails,
/// SQS for queues), aws-lambda-tools-defaults.json and the Lambda packages/properties in the .csproj.
/// The original Main() is kept so the automation can still run locally or as an ECS task.
/// </summary>
public static class LambdaScaffolder
{
    // Fallback versions when nuget.org is not consulted; the verification build restores whatever is current.
    private static readonly (string Id, string Version)[] CorePackages =
    [
        ("Amazon.Lambda.Core", "2.5.1"),
        ("Amazon.Lambda.Serialization.SystemTextJson", "2.4.4")
    ];

    public static void Scaffold(ProjectInfo project, ApplicationProfile profile, ProjectFileSpec spec, OutputPlan plan, string relativeDir, List<InventoryItem> items)
    {
        var fileDriven = profile.HasAny(Signal.FileWatcher, Signal.Ftp, Signal.MailboxReading) || profile.HasAny(Signal.FileSystemWrites, Signal.UncPaths, Signal.WindowsPaths) && profile.Has(Signal.SpreadsheetFiles);
        var queueDriven = profile.HasAny(Signal.Msmq, Signal.RabbitMq, Signal.MessageBusFramework, Signal.Kafka, Signal.AzureServiceBus);
        if (!fileDriven && !queueDriven) fileDriven = true;

        var ns = string.IsNullOrWhiteSpace(project.RootNamespace) ? project.Name : project.RootNamespace;
        var assembly = string.IsNullOrWhiteSpace(project.AssemblyName) ? project.Name : project.AssemblyName;
        var handler = fileDriven ? "FunctionHandler" : "QueueHandler";

        plan.Write(Path.Combine(relativeDir, "Function.cs"), FunctionSource(ns, fileDriven, queueDriven, profile.Has(Signal.MailboxReading)));
        plan.Write(Path.Combine(relativeDir, "aws-lambda-tools-defaults.json"), ToolsDefaults(assembly, ns, handler));

        foreach (var (id, version) in CorePackages.Concat(fileDriven ? [("Amazon.Lambda.S3Events", "3.1.1")] : []).Concat(queueDriven ? [("Amazon.Lambda.SQSEvents", "2.2.0")] : []))
            if (spec.Packages.All(p => !p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                spec.Packages.Add(new PackageReferenceOut(id, version));
        foreach (var (name, value) in new[] { ("AWSProjectType", "Lambda"), ("GenerateRuntimeConfigurationFiles", "true"), ("CopyLocalLockFileAssemblies", "true") })
            if (spec.Properties.All(p => p.Name != name)) spec.Properties.Add((name, value));

        items.Add(new InventoryItem
        {
            Project = project.Name, Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "AWS-LAMBDA",
            Title = $"Recomendado como AWS Lambda: Function.cs ({(fileDriven ? "S3Event" : "")}{(fileDriven && queueDriven ? " + " : "")}{(queueDriven ? "SQSEvent" : "")}) e aws-lambda-tools-defaults.json gerados",
            Description = "O Main() original foi mantido (roda localmente ou como tarefa ECS). Pacotes Amazon.Lambda.* adicionados com versões de referência; o restore do build de verificação confirma.",
            Suggestion = "Ligue o handler à lógica existente (TODO no Function.cs), publique com 'dotnet lambda deploy-function' (Amazon.Lambda.Tools) e configure o gatilho indicado na arquitetura. Se o runtime gerenciado .NET 10 não estiver disponível na região, publique como imagem de container (--package-type image).",
            AutoMigrated = true, FilePath = "Function.cs"
        });
    }

    private static string FunctionSource(string ns, bool fileDriven, bool queueDriven, bool mailbox)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Amazon.Lambda.Core;");
        if (fileDriven) sb.AppendLine("using Amazon.Lambda.S3Events;");
        if (queueDriven) sb.AppendLine("using Amazon.Lambda.SQSEvents;");
        sb.AppendLine();
        sb.AppendLine("// Serializador JSON dos eventos do Lambda");
        sb.AppendLine("[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Gerado pelo Migrator: ponto de entrada da automação na AWS Lambda. O Main() original continua existindo");
        sb.AppendLine("    /// para execução local/ECS; aqui cada evento vira uma chamada à mesma lógica de negócio.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public class Function");
        sb.AppendLine("    {");
        if (fileDriven)
        {
            sb.AppendLine(mailbox
                ? "        /// <summary>Gatilho: S3 Event Notifications (arquivos de entrada e e-mails recebidos pelo Amazon SES e gravados no bucket).</summary>"
                : "        /// <summary>Gatilho: S3 Event Notifications (ObjectCreated) do bucket de entrada.</summary>");
            sb.AppendLine("        public async Task FunctionHandler(S3Event evt, ILambdaContext context)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var record in evt.Records ?? [])");
            sb.AppendLine("            {");
            sb.AppendLine("                var bucket = record.S3.Bucket.Name;");
            sb.AppendLine("                var key = System.Net.WebUtility.UrlDecode(record.S3.Object.Key);");
            sb.AppendLine("                context.Logger.LogInformation($\"Processando s3://{bucket}/{key}\");");
            sb.AppendLine("                // TODO Migrator: baixe o objeto (AWSSDK.S3 GetObjectAsync) e chame a lógica que antes lia o arquivo da pasta de entrada.");
            sb.AppendLine("                // Ex.: using var s3 = new Amazon.S3.AmazonS3Client(); using var obj = await s3.GetObjectAsync(bucket, key); ... processar(obj.ResponseStream)");
            sb.AppendLine("                await Task.CompletedTask;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }
        if (queueDriven)
        {
            if (fileDriven) sb.AppendLine();
            sb.AppendLine("        /// <summary>Gatilho: fila Amazon SQS (substitui MSMQ/RabbitMQ). Lance exceção para a mensagem voltar à fila/DLQ.</summary>");
            sb.AppendLine("        public async Task QueueHandler(SQSEvent evt, ILambdaContext context)");
            sb.AppendLine("        {");
            sb.AppendLine("            foreach (var message in evt.Records ?? [])");
            sb.AppendLine("            {");
            sb.AppendLine("                context.Logger.LogInformation($\"Mensagem {message.MessageId}\");");
            sb.AppendLine("                // TODO Migrator: desserialize message.Body e chame a lógica que antes consumia a fila.");
            sb.AppendLine("                await Task.CompletedTask;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private static string ToolsDefaults(string assembly, string ns, string handler) => new JsonObject
    {
        ["Information"] = new JsonArray("Gerado pelo Migrator. Publique com: dotnet lambda deploy-function (dotnet tool install -g Amazon.Lambda.Tools).",
            "Se o runtime gerenciado .NET 10 não estiver disponível, use --package-type image com uma imagem base public.ecr.aws/lambda/dotnet."),
        ["profile"] = "default",
        ["region"] = "sa-east-1",
        ["configuration"] = "Release",
        ["function-runtime"] = "dotnet10",
        ["function-memory-size"] = 512,
        ["function-timeout"] = 300,
        ["function-handler"] = $"{assembly}::{ns}.Function::{handler}",
        ["function-architecture"] = "arm64"
    }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}
