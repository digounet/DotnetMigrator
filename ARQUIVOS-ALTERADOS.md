# Arquivos alterados (lista consolidada)

Todos os arquivos criados, alterados, renomeados ou excluidos desde o commit anterior a `6cea52b` (base `4727f6f`) ate `4c0e318`, para levar as mudancas manualmente ao repositorio do banco. O status e o resultado final do intervalo, nao de cada commit: um arquivo criado e depois alterado aparece como criado; criado e depois excluido nao aparece.

Para regenerar: `git diff --name-status -M <base> HEAD` (o `<base>` e o commit anterior ao primeiro que voce ainda nao levou).

**Total: 79 arquivos** (34 criados, 45 alterados, 0 renomeados, 0 excluidos).

## Criados (34)

- `ARQUIVOS-ALTERADOS.md`
- `samples/LegacyShop/lib/Legacy.Impressao.dll`
- `samples/LegacyShop/lib/README.md`
- `samples/LegacyShop/lib/src/Legacy.Impressao/ImpressoraEtiquetas.cs`
- `src/Migrator.Core/Analysis/DataAccessAnalyzer.cs`
- `src/Migrator.Core/Cloud/AwsArchitect.Framework.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.Ec2.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.EcsService.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.Guide.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.Service.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.cs`
- `src/Migrator.Core/Llm/CorporateApiAssistant.cs`
- `src/Migrator.Core/Migration/ConfigSettingsCollector.cs`
- `src/Migrator.Core/Migration/LiteralExternalizer.cs`
- `src/Migrator.Core/Migration/ModernProjectUpdater.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Binaries.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Framework.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Settings.cs`
- `src/Migrator.Core/Models/DataAccess.cs`
- `src/Migrator.Core/Models/DeploymentGuide.cs`
- `src/Migrator.Core/Models/ExternalizedSetting.cs`
- `src/Migrator.Core/NuGet/NuGetSource.cs`
- `src/Migrator.Core/Reporting/ExcelReport.Data.cs`
- `src/Migrator.Core/Reporting/ExcelReport.Deployment.cs`
- `src/Migrator.Core/Reporting/HtmlReport.Data.cs`
- `src/Migrator.Core/Reporting/HtmlReport.Deployment.cs`
- `src/Migrator.Core/Reporting/MarkdownReport.Data.cs`
- `src/Migrator.Core/Reporting/MarkdownReport.Deployment.cs`
- `tests/Migrator.Tests/AssemblyInspectorTests.cs`
- `tests/Migrator.Tests/CorporateApiAssistantTests.cs`
- `tests/Migrator.Tests/DataAccessAnalyzerTests.cs`
- `tests/Migrator.Tests/FrameworkTargetTests.cs`
- `tests/Migrator.Tests/LiteralExternalizerTests.cs`
- `tests/Migrator.Tests/NuGetSourceTests.cs`

## Alterados (45)

- `ARQUIVOS-ALTERADOS.md`
- `CLAUDE.md`
- `README.md`
- `docs/demo/migration-report.html`
- `docs/demo/migration-report.md`
- `docs/index.html`
- `samples/LegacyShop/LegacyShop.Relatorios/GeradorRelatorio.vb`
- `samples/LegacyShop/LegacyShop.Relatorios/LegacyShop.Relatorios.vbproj`
- `samples/LegacyShop/LegacyShop.Web/Helpers/AppConfig.cs`
- `src/Migrator.Cli/Program.cs`
- `src/Migrator.Cli/RunCommand.cs`
- `src/Migrator.Core/Analysis/ApplicationProfiler.cs`
- `src/Migrator.Core/Analysis/AssemblyInspector.cs`
- `src/Migrator.Core/Cloud/AwsArchitect.cs`
- `src/Migrator.Core/Cloud/InfrastructureGenerator.cs`
- `src/Migrator.Core/Llm/ILlmAssistant.cs`
- `src/Migrator.Core/Llm/LlmAssistantFactory.cs`
- `src/Migrator.Core/Migration/MigrationEngine.cs`
- `src/Migrator.Core/Migration/PackagePlanner.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.cs`
- `src/Migrator.Core/Migration/SecretsExtractor.cs`
- `src/Migrator.Core/Models/CloudArchitecture.cs`
- `src/Migrator.Core/Models/MigrationResult.cs`
- `src/Migrator.Core/NuGet/NuGetClient.cs`
- `src/Migrator.Core/Portfolio/PortfolioModels.cs`
- `src/Migrator.Core/Portfolio/PortfolioRunner.cs`
- `src/Migrator.Core/Reporting/CsvReport.cs`
- `src/Migrator.Core/Reporting/ExcelReport.cs`
- `src/Migrator.Core/Reporting/HtmlReport.Cloud.cs`
- `src/Migrator.Core/Reporting/HtmlReport.cs`
- `src/Migrator.Core/Reporting/JsonReport.cs`
- `src/Migrator.Core/Reporting/MarkdownReport.Cloud.cs`
- `src/Migrator.Core/Reporting/MarkdownReport.cs`
- `src/Migrator.Core/Reporting/ReportWriter.cs`
- `tests/Migrator.Tests/IacGenerationTests.cs`
- `tests/Migrator.Tests/LlmTests.cs`
- `tests/Migrator.Tests/ModernizationAndCloudTests.cs`
- `tests/Migrator.Tests/PortfolioTests.cs`
- `tests/Migrator.Tests/SampleSolutionTests.cs`
- `tests/Migrator.Tests/SnapshotTests.cs`
- `tests/Migrator.Tests/Snapshots/.github_workflows_deploy.yml.snap`
- `tests/Migrator.Tests/Snapshots/LegacyShop.Web_Dockerfile.snap`
- `tests/Migrator.Tests/Snapshots/LegacyShop.Web_appsettings.json.snap`
- `tests/Migrator.Tests/Snapshots/infra_terraform_ecs.tf.snap`
- `tests/Migrator.Tests/Snapshots/infra_terraform_lambda.tf.snap`

## Como aplicar no banco

1. Copie os arquivos **criados** e **alterados** para o mesmo caminho relativo no repositorio do banco (substituindo os existentes).
2. Exclua os **excluidos** e aplique os **renomeados** (apague o caminho antigo, crie o novo).
3. `dotnet build Migrator.slnx -warnaserror` e `dotnet test Migrator.slnx` devem passar (165 testes).
4. Preencha `DefaultEndpoint`/`DefaultTokenUrl` em `src/Migrator.Core/Llm/CorporateApiAssistant.cs` com o gateway do banco.
