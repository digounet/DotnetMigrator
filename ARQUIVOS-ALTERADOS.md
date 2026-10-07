# Arquivos alterados por commit

Lista dos arquivos criados, alterados ou excluidos em cada commit da evolucao para o layout da plataforma (lift-and-shift, CloudFormation, dados acessados, LLM corporativa). Gerada com `git show --name-status`; a secao do ultimo commit foi montada a partir do `git status` antes dele ser feito.

## 6cea52b (2026-10-04) — feat: private NuGet feed (nuget.config) support and net10.0 upgrade of already-modern projects

| Status | Arquivo |
|---|---|
| alterado | `CLAUDE.md` |
| alterado | `README.md` |
| alterado | `src/Migrator.Cli/Program.cs` |
| alterado | `src/Migrator.Cli/RunCommand.cs` |
| alterado | `src/Migrator.Core/Cloud/AwsArchitect.cs` |
| alterado | `src/Migrator.Core/Migration/MigrationEngine.cs` |
| criado | `src/Migrator.Core/Migration/ModernProjectUpdater.cs` |
| alterado | `src/Migrator.Core/Migration/PackagePlanner.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.cs` |
| alterado | `src/Migrator.Core/Models/MigrationResult.cs` |
| alterado | `src/Migrator.Core/NuGet/NuGetClient.cs` |
| criado | `src/Migrator.Core/NuGet/NuGetSource.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioModels.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioRunner.cs` |
| alterado | `src/Migrator.Core/Reporting/HtmlReport.cs` |
| alterado | `src/Migrator.Core/Reporting/MarkdownReport.cs` |
| alterado | `src/Migrator.Core/Reporting/ReportWriter.cs` |
| criado | `tests/Migrator.Tests/NuGetSourceTests.cs` |
| alterado | `tests/Migrator.Tests/Snapshots/LegacyShop.Web_Dockerfile.snap` |

19 arquivos.

## cde9355 (2026-10-06) — feat: destino .NET Framework 4.8.1 com CloudFormation e inventario de dados acessados

| Status | Arquivo |
|---|---|
| alterado | `CLAUDE.md` |
| alterado | `README.md` |
| alterado | `docs/demo/migration-report.html` |
| alterado | `docs/demo/migration-report.md` |
| alterado | `src/Migrator.Cli/Program.cs` |
| alterado | `src/Migrator.Cli/RunCommand.cs` |
| criado | `src/Migrator.Core/Analysis/DataAccessAnalyzer.cs` |
| criado | `src/Migrator.Core/Cloud/AwsArchitect.Framework.cs` |
| alterado | `src/Migrator.Core/Cloud/AwsArchitect.cs` |
| criado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Ec2.cs` |
| criado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Ecs.cs` |
| criado | `src/Migrator.Core/Cloud/CloudFormationGenerator.cs` |
| alterado | `src/Migrator.Core/Cloud/InfrastructureGenerator.cs` |
| alterado | `src/Migrator.Core/Migration/MigrationEngine.cs` |
| criado | `src/Migrator.Core/Migration/ProjectMigrator.Framework.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.cs` |
| alterado | `src/Migrator.Core/Models/CloudArchitecture.cs` |
| criado | `src/Migrator.Core/Models/DataAccess.cs` |
| alterado | `src/Migrator.Core/Models/MigrationResult.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioModels.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioRunner.cs` |
| alterado | `src/Migrator.Core/Reporting/CsvReport.cs` |
| criado | `src/Migrator.Core/Reporting/ExcelReport.Data.cs` |
| alterado | `src/Migrator.Core/Reporting/ExcelReport.cs` |
| criado | `src/Migrator.Core/Reporting/HtmlReport.Data.cs` |
| alterado | `src/Migrator.Core/Reporting/HtmlReport.cs` |
| alterado | `src/Migrator.Core/Reporting/JsonReport.cs` |
| criado | `src/Migrator.Core/Reporting/MarkdownReport.Data.cs` |
| alterado | `src/Migrator.Core/Reporting/MarkdownReport.cs` |
| alterado | `src/Migrator.Core/Reporting/ReportWriter.cs` |
| criado | `tests/Migrator.Tests/DataAccessAnalyzerTests.cs` |
| criado | `tests/Migrator.Tests/FrameworkTargetTests.cs` |
| alterado | `tests/Migrator.Tests/SampleSolutionTests.cs` |

33 arquivos.

## 6ad6bc4 (2026-10-06) — feat: lift-and-shift como padrao, literais externalizados e CloudFormation no layout da plataforma

| Status | Arquivo |
|---|---|
| alterado | `CLAUDE.md` |
| alterado | `README.md` |
| alterado | `docs/demo/migration-report.html` |
| alterado | `docs/demo/migration-report.md` |
| alterado | `samples/LegacyShop/LegacyShop.Web/Helpers/AppConfig.cs` |
| alterado | `src/Migrator.Cli/Program.cs` |
| alterado | `src/Migrator.Core/Cloud/AwsArchitect.Framework.cs` |
| alterado | `src/Migrator.Core/Cloud/AwsArchitect.cs` |
| alterado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Ec2.cs` |
| excluido | `src/Migrator.Core/Cloud/CloudFormationGenerator.Ecs.cs` |
| criado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Service.cs` |
| alterado | `src/Migrator.Core/Cloud/CloudFormationGenerator.cs` |
| alterado | `src/Migrator.Core/Cloud/InfrastructureGenerator.cs` |
| criado | `src/Migrator.Core/Migration/ConfigSettingsCollector.cs` |
| criado | `src/Migrator.Core/Migration/LiteralExternalizer.cs` |
| alterado | `src/Migrator.Core/Migration/MigrationEngine.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.Framework.cs` |
| criado | `src/Migrator.Core/Migration/ProjectMigrator.Settings.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.cs` |
| alterado | `src/Migrator.Core/Migration/SecretsExtractor.cs` |
| criado | `src/Migrator.Core/Models/ExternalizedSetting.cs` |
| alterado | `src/Migrator.Core/Models/MigrationResult.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioModels.cs` |
| alterado | `src/Migrator.Core/Portfolio/PortfolioRunner.cs` |
| alterado | `tests/Migrator.Tests/FrameworkTargetTests.cs` |
| alterado | `tests/Migrator.Tests/IacGenerationTests.cs` |
| criado | `tests/Migrator.Tests/LiteralExternalizerTests.cs` |
| alterado | `tests/Migrator.Tests/LlmTests.cs` |
| alterado | `tests/Migrator.Tests/ModernizationAndCloudTests.cs` |
| alterado | `tests/Migrator.Tests/NuGetSourceTests.cs` |
| alterado | `tests/Migrator.Tests/PortfolioTests.cs` |
| alterado | `tests/Migrator.Tests/SampleSolutionTests.cs` |
| alterado | `tests/Migrator.Tests/SnapshotTests.cs` |
| alterado | `tests/Migrator.Tests/Snapshots/LegacyShop.Web_appsettings.json.snap` |
| alterado | `tests/Migrator.Tests/Snapshots/infra_terraform_ecs.tf.snap` |
| alterado | `tests/Migrator.Tests/Snapshots/infra_terraform_lambda.tf.snap` |

36 arquivos.

## ea6fd1e (2026-10-07) — feat: layout do repositorio do banco, service.yml fiel a plataforma e LLM corporativa via API

| Status | Arquivo |
|---|---|
| alterado | `CLAUDE.md` |
| alterado | `README.md` |
| alterado | `docs/demo/migration-report.html` |
| alterado | `docs/demo/migration-report.md` |
| alterado | `src/Migrator.Cli/Program.cs` |
| alterado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Ec2.cs` |
| criado | `src/Migrator.Core/Cloud/CloudFormationGenerator.EcsService.cs` |
| alterado | `src/Migrator.Core/Cloud/CloudFormationGenerator.Service.cs` |
| alterado | `src/Migrator.Core/Cloud/CloudFormationGenerator.cs` |
| alterado | `src/Migrator.Core/Cloud/InfrastructureGenerator.cs` |
| criado | `src/Migrator.Core/Llm/CorporateApiAssistant.cs` |
| alterado | `src/Migrator.Core/Llm/ILlmAssistant.cs` |
| alterado | `src/Migrator.Core/Llm/LlmAssistantFactory.cs` |
| alterado | `src/Migrator.Core/Migration/MigrationEngine.cs` |
| alterado | `src/Migrator.Core/Models/MigrationResult.cs` |
| criado | `tests/Migrator.Tests/CorporateApiAssistantTests.cs` |
| alterado | `tests/Migrator.Tests/FrameworkTargetTests.cs` |
| alterado | `tests/Migrator.Tests/IacGenerationTests.cs` |
| alterado | `tests/Migrator.Tests/NuGetSourceTests.cs` |
| alterado | `tests/Migrator.Tests/SampleSolutionTests.cs` |
| alterado | `tests/Migrator.Tests/SnapshotTests.cs` |
| alterado | `tests/Migrator.Tests/Snapshots/.github_workflows_deploy.yml.snap` |

22 arquivos.

## 85f97d5 (2026-10-07) — docs: README reestruturado, GitHub Pages e lista de arquivos por commit

| Status | Arquivo |
|---|---|
| criado | `ARQUIVOS-ALTERADOS.md` |
| alterado | `README.md` |
| alterado | `docs/index.html` |
| alterado | `src/Migrator.Cli/Program.cs` |

4 arquivos.

## Commit seguinte — feat: inspecao de DLLs locais por tipo, membro e P/Invoke

| Status | Arquivo |
|---|---|
| alterado | `ARQUIVOS-ALTERADOS.md` |
| alterado | `CLAUDE.md` |
| alterado | `README.md` |
| alterado | `docs/demo/migration-report.html` |
| alterado | `docs/demo/migration-report.md` |
| criado | `samples/LegacyShop/lib/Legacy.Impressao.dll` |
| criado | `samples/LegacyShop/lib/README.md` |
| criado | `samples/LegacyShop/lib/src/Legacy.Impressao/ImpressoraEtiquetas.cs` |
| alterado | `samples/LegacyShop/LegacyShop.Relatorios/GeradorRelatorio.vb` |
| alterado | `samples/LegacyShop/LegacyShop.Relatorios/LegacyShop.Relatorios.vbproj` |
| alterado | `src/Migrator.Core/Analysis/ApplicationProfiler.cs` |
| alterado | `src/Migrator.Core/Analysis/AssemblyInspector.cs` |
| criado | `src/Migrator.Core/Migration/ProjectMigrator.Binaries.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.Framework.cs` |
| alterado | `src/Migrator.Core/Migration/ProjectMigrator.cs` |
| criado | `tests/Migrator.Tests/AssemblyInspectorTests.cs` |
| alterado | `tests/Migrator.Tests/FrameworkTargetTests.cs` |
| alterado | `tests/Migrator.Tests/SampleSolutionTests.cs` |

18 arquivos.
