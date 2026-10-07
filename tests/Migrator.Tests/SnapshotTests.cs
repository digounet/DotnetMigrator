using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>
/// Golden files for the sample's generated output. Any rule change that alters a generated file shows up here as a diff.
/// To accept new output: MIGRATOR_UPDATE_SNAPSHOTS=1 dotnet test --filter SnapshotTests
/// </summary>
public sealed class SnapshotTests : IDisposable
{
    private static readonly string[] Files =
    [
        "LegacyShop.Web/LegacyShop.Web.csproj",
        "LegacyShop.Web/Program.cs",
        "LegacyShop.Web/Dockerfile",
        "LegacyShop.Web/appsettings.json",
        "LegacyShop.Core/LegacyShop.Core.csproj",
        "LegacyShop.Core/Services/ProdutoService.cs",
        "LegacyShop.Worker/LegacyShop.Worker.csproj",
        "LegacyShop.Worker/Program.cs",
        "LegacyShop.Worker/SincronizacaoService.cs",
        "LegacyShop.Importador/LegacyShop.Importador.csproj",
        "LegacyShop.Importador/Program.cs",
        "LegacyShop.Importador/Function.cs",
        "LegacyShop.slnx",
        "infra/terraform/ecs.tf",
        "infra/terraform/lambda.tf",
        ".github/workflows/deploy.yml"
    ];

    private readonly string _work = Directory.CreateTempSubdirectory("migrator-snapshots-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public async Task Generated_output_matches_the_golden_files()
    {
        var root = RepoRoot();
        var snapshots = Path.Combine(root, "tests", "Migrator.Tests", "Snapshots");
        var update = Environment.GetEnvironmentVariable("MIGRATOR_UPDATE_SNAPSHOTS") == "1";
        var output = Path.Combine(_work, "out");
        await new MigrationEngine().RunAsync(new MigrationOptions { Target = MigrationTarget.Net10, InputPath = Path.Combine(root, "samples", "LegacyShop", "LegacyShop.sln"), OutputDir = output, Offline = true, VerifyBuild = false, Serverless = true
        });

        var differences = new List<string>();
        foreach (var relative in Files)
        {
            var actualPath = Path.Combine(output, relative);
            Assert.True(File.Exists(actualPath), $"arquivo não gerado: {relative}");
            var actual = Normalize(await File.ReadAllTextAsync(actualPath));
            var snapshotPath = Path.Combine(snapshots, relative.Replace('/', '_') + ".snap");
            if (update || !File.Exists(snapshotPath))
            {
                Directory.CreateDirectory(snapshots);
                await File.WriteAllTextAsync(snapshotPath, actual);
                continue;
            }
            var expected = Normalize(await File.ReadAllTextAsync(snapshotPath));
            if (expected != actual) differences.Add($"{relative}\n{FirstDifference(expected, actual)}");
        }
        Assert.True(differences.Count == 0,
            "Saída gerada diferente dos snapshots (se a mudança é intencional, rode com MIGRATOR_UPDATE_SNAPSHOTS=1):\n\n" + string.Join("\n\n", differences));
    }

    private static string Normalize(string text) =>
        string.Join("\n", text.Replace("﻿", "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).TrimEnd() + "\n";

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var el = i < e.Length ? e[i] : "<fim>";
            var al = i < a.Length ? a[i] : "<fim>";
            if (el != al) return $"  linha {i + 1}\n  esperado: {el}\n  obtido:   {al}";
        }
        return "  (sem diferença de linha; apenas espaços finais)";
    }
}
