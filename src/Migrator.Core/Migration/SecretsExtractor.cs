using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Migrator.Core.Migration;

/// <summary>One credential moved out of a generated appsettings*.json.</summary>
/// <param name="ConfigPath">IConfiguration path, e.g. <c>ConnectionStrings:DefaultConnection</c>.</param>
/// <param name="EnvironmentVariable">Equivalent environment variable (<c>ConnectionStrings__DefaultConnection</c>), what the ECS task definition injects.</param>
/// <param name="SecretName">Name in AWS Secrets Manager (<c>legacyshop/legacyshop-web/ConnectionStrings/DefaultConnection</c>).</param>
/// <param name="Environment">null for appsettings.json, otherwise the ASPNETCORE_ENVIRONMENT of the file it came from.</param>
public sealed record ExtractedSecret(string ConfigPath, string EnvironmentVariable, string SecretName, string Value, string? Environment);

public sealed class SecretsPlan
{
    public List<ExtractedSecret> Secrets { get; } = [];
    public bool Any => Secrets.Count > 0;
}

/// <summary>
/// Removes credentials from the generated appsettings*.json (replacing them with an explicit placeholder) and produces the
/// files a team needs to put them where they belong: a template with the values, scripts to create them in AWS Secrets
/// Manager, the <c>secrets</c> block of an ECS task definition and a <c>dotnet user-secrets</c> script for development.
/// Nothing with a real credential is ever placed inside a project folder, so it cannot end up in a Docker image.
/// </summary>
public static partial class SecretsExtractor
{
    public const string RootFolder = "_secrets";

    public static SecretsPlan Extract(ConfigMigrationResult config, string solutionName, string projectName)
    {
        var plan = new SecretsPlan();
        var prefix = $"{Slug(solutionName)}/{Slug(projectName)}";
        if (config.AppSettingsJson != null)
            config.AppSettingsJson = Scrub(config.AppSettingsJson, prefix, null, plan);
        foreach (var environment in config.EnvironmentJson.Keys.ToList())
            config.EnvironmentJson[environment] = Scrub(config.EnvironmentJson[environment], prefix, environment, plan);
        return plan;
    }

    /// <summary>A credential found in code (not in a config file): same naming as the config ones, so scripts and IaC treat it alike.</summary>
    public static ExtractedSecret ForCode(string solutionName, string projectName, string configPath, string value) =>
        new(configPath, configPath.Replace(":", "__"), $"{Slug(solutionName)}/{Slug(projectName)}/{configPath.Replace(':', '/')}", value, null);

    public const string PlaceholderPrefix = "<secret: ";

    private static string Scrub(string json, string prefix, string? environment, SecretsPlan plan)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return json; }
        if (root is not JsonObject obj) return json;
        Walk(obj, "", prefix, environment, plan);
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static void Walk(JsonObject obj, string path, string prefix, string? environment, SecretsPlan plan)
    {
        foreach (var key in obj.Select(kv => kv.Key).ToList())
        {
            var node = obj[key];
            var configPath = path.Length == 0 ? key : $"{path}:{key}";
            switch (node)
            {
                case JsonObject child:
                    Walk(child, configPath, prefix, environment, plan);
                    break;
                case JsonValue value when value.TryGetValue<string>(out var text) && text.Length >= 3:
                    var isSecretKey = SecretKey().IsMatch(key) && !key.Contains("publicKeyToken", StringComparison.OrdinalIgnoreCase);
                    var isConnectionStringWithPassword = (path.StartsWith("ConnectionStrings", StringComparison.OrdinalIgnoreCase) || key.EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase))
                                                         && PasswordInConnectionString().IsMatch(text);
                    if (!isSecretKey && !isConnectionStringWithPassword) break;
                    if (text.StartsWith("<secret:", StringComparison.Ordinal)) break;
                    var secretName = prefix + (environment != null ? $"/{Slug(environment)}" : "") + "/" + configPath.Replace(':', '/');
                    plan.Secrets.Add(new ExtractedSecret(configPath, configPath.Replace(":", "__"), secretName, text, environment));
                    obj[key] = $"<secret: {secretName}>";
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- artifacts

    /// <summary>
    /// The removed values in the shape of appsettings.json (same nesting), for the given environment (null = base file).
    /// Paste into the local appsettings, or load with AddJsonFile("appsettings.Secrets.json", optional: true) outside the repository.
    /// </summary>
    public static string AppSettingsShapedJson(SecretsPlan plan, string? environment)
    {
        var root = new JsonObject();
        foreach (var s in plan.Secrets.Where(s => s.Environment == environment))
        {
            var parts = s.ConfigPath.Split(':');
            var current = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (current[parts[i]] is not JsonObject next) current[parts[i]] = next = new JsonObject();
                current = next;
            }
            current[parts[^1]] = s.Value;
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static IEnumerable<string?> Environments(SecretsPlan plan) => plan.Secrets.Select(s => s.Environment).Distinct();

    public static string TemplateJson(SecretsPlan plan)
    {
        var root = new JsonObject();
        foreach (var s in plan.Secrets) root[s.SecretName] = s.Value;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string CreateScriptBash(SecretsPlan plan, string projectName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine($"# Cria no AWS Secrets Manager os segredos retirados do appsettings de {projectName}.");
        sb.AppendLine("# Uso: AWS_REGION=sa-east-1 ./create-secrets.sh   (requer AWS CLI autenticada)");
        sb.AppendLine("# Os valores vêm de secrets.template.json; edite-os antes se forem os de produção.");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine("cd \"$(dirname \"$0\")\"");
        sb.AppendLine("REGION=\"${AWS_REGION:-sa-east-1}\"");
        sb.AppendLine("create() {");
        sb.AppendLine("  local name=\"$1\" key=\"$2\"");
        sb.AppendLine("  local value; value=$(python3 -c 'import json,sys; print(json.load(open(\"secrets.template.json\"))[sys.argv[1]])' \"$key\")");
        sb.AppendLine("  if aws secretsmanager describe-secret --secret-id \"$name\" --region \"$REGION\" >/dev/null 2>&1; then");
        sb.AppendLine("    aws secretsmanager put-secret-value --secret-id \"$name\" --secret-string \"$value\" --region \"$REGION\" >/dev/null && echo \"atualizado: $name\"");
        sb.AppendLine("  else");
        sb.AppendLine("    aws secretsmanager create-secret --name \"$name\" --secret-string \"$value\" --region \"$REGION\" >/dev/null && echo \"criado: $name\"");
        sb.AppendLine("  fi");
        sb.AppendLine("}");
        foreach (var s in plan.Secrets) sb.AppendLine($"create \"{s.SecretName}\" \"{s.SecretName}\"");
        return sb.ToString().Replace("\r\n", "\n");
    }

    public static string CreateScriptPowerShell(SecretsPlan plan, string projectName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Cria no AWS Secrets Manager os segredos retirados do appsettings de {projectName}.");
        sb.AppendLine("# Uso: $env:AWS_REGION='sa-east-1'; .\\create-secrets.ps1   (requer AWS CLI autenticada)");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Set-Location $PSScriptRoot");
        sb.AppendLine("$region = if ($env:AWS_REGION) { $env:AWS_REGION } else { 'sa-east-1' }");
        sb.AppendLine("$values = Get-Content secrets.template.json -Raw | ConvertFrom-Json -AsHashtable");
        sb.AppendLine("foreach ($name in @(");
        sb.AppendLine(string.Join(",\n", plan.Secrets.Select(s => $"    '{s.SecretName}'")));
        sb.AppendLine(")) {");
        sb.AppendLine("    $value = $values[$name]");
        sb.AppendLine("    aws secretsmanager describe-secret --secret-id $name --region $region *> $null");
        sb.AppendLine("    if ($LASTEXITCODE -eq 0) { aws secretsmanager put-secret-value --secret-id $name --secret-string $value --region $region | Out-Null; Write-Host \"atualizado: $name\" }");
        sb.AppendLine("    else { aws secretsmanager create-secret --name $name --secret-string $value --region $region | Out-Null; Write-Host \"criado: $name\" }");
        sb.AppendLine("}");
        return sb.ToString().Replace("\r\n", "\n");
    }

    /// <summary>The <c>secrets</c> array of an ECS container definition: each one becomes the environment variable the .NET configuration reads.</summary>
    public static string EcsTaskSecretsJson(SecretsPlan plan)
    {
        var array = new JsonArray();
        foreach (var s in plan.Secrets.Where(s => s.Environment is null or "Production").DistinctBy(s => s.EnvironmentVariable))
            array.Add(new JsonObject
            {
                ["name"] = s.EnvironmentVariable,
                ["valueFrom"] = $"arn:aws:secretsmanager:${{AWS_REGION}}:${{AWS_ACCOUNT_ID}}:secret:{s.SecretName}"
            });
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string UserSecretsScript(SecretsPlan plan, string projectRelativePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# Desenvolvimento local: grava os segredos no cofre do usuário (fora do repositório). O UserSecretsId já está no .csproj.");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine($"PROJ=\"$(dirname \"$0\")/../../{projectRelativePath.Replace('\\', '/')}\"");
        sb.AppendLine("dotnet user-secrets init --project \"$PROJ\" >/dev/null");
        foreach (var s in plan.Secrets.Where(s => s.Environment is null or "Development").DistinctBy(s => s.ConfigPath))
            sb.AppendLine($"dotnet user-secrets set \"{s.ConfigPath}\" '{s.Value.Replace("'", "'\\''")}' --project \"$PROJ\"");
        return sb.ToString().Replace("\r\n", "\n");
    }

    public static string Readme(SecretsPlan plan, string projectName) =>
        $"""
        # Segredos de {projectName}

        O Migrator retirou {plan.Secrets.Count} credencial(is) dos arquivos appsettings*.json gerados e deixou no lugar
        um marcador `<secret: nome>`. A aplicação lê esses valores de variáveis de ambiente (`Secao__Chave`), que na AWS
        vêm do Secrets Manager via task definition do ECS (ou do Lambda), e em desenvolvimento do `dotnet user-secrets`.

        Esta pasta NÃO deve ir para o repositório nem para a imagem Docker (já está no .gitignore e no .dockerignore gerados).

        | Arquivo | Uso |
        |---|---|
        | `appsettings.Secrets.json` (e `appsettings.<Ambiente>.Secrets.json`) | os valores retirados, na mesma estrutura do appsettings: cole no appsettings local ou carregue com `AddJsonFile("appsettings.Secrets.json", optional: true)` a partir de uma pasta fora do repositório |
        | `secrets.template.json` | nome do segredo → valor que estava no web.config/app.config |
        | `create-secrets.sh` / `create-secrets.ps1` | cria/atualiza os segredos no AWS Secrets Manager |
        | `ecs-task-secrets.json` | bloco `secrets` para o container definition (substitua REGION/ACCOUNT) |
        | `set-user-secrets.sh` | grava os valores no user-secrets para rodar localmente |

        Segredos:
        {string.Join("\n", plan.Secrets.Select(s => $"- `{s.ConfigPath}`{(s.Environment != null ? $" ({s.Environment})" : "")} → `{s.SecretName}` → variável `{s.EnvironmentVariable}`"))}

        Depois de criar os segredos, rotacione as credenciais que estavam em texto claro no repositório antigo.
        """.Replace("\r\n", "\n");

    /// <summary>Stable UserSecretsId per project so re-running the migration does not change the .csproj.</summary>
    public static string UserSecretsId(string solutionName, string projectName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{solutionName}/{projectName}"));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40); // version 4 layout, deterministic content
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString();
    }

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9._-]+", "-").Trim('-');

    [GeneratedRegex(@"password|pwd|secret|apikey|api_key|api-key|clientsecret|accesskey|access_key|token|senha|chave|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"(password|pwd)\s*=\s*[^;]{1,}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInConnectionString();
}
