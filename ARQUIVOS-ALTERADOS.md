# Arquivos alterados (lista consolidada)

Arquivos criados, alterados, renomeados ou excluidos **a partir da solicitacao de 2026-10-07 sobre solucoes mistas e appsettings** (base: commit `ee42249`, o ultimo ja levado ao banco), para aplicar manualmente no repositorio do banco. O status e o resultado final do intervalo, nao de cada commit.

Para regenerar depois de novos commits: `git diff --name-status -M ee42249 HEAD` (troque a base pelo ultimo commit ja levado).

**Total: 34 arquivos** (5 criados, 29 alterados, 0 renomeados, 0 excluidos).

## Criados (5)

- `samples/LegacyShop/LegacyShop.Comum/`
- `samples/LegacyShop/LegacyShop.Robo/`
- `src/Migrator.Core/Migration/AppSettingsAnalyzer.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Modern.cs`
- `tests/Migrator.Tests/AppSettingsAnalyzerTests.cs`

## Alterados (29)

- `ARQUIVOS-ALTERADOS.md`
- `CLAUDE.md`
- `README.md`
- `docs/demo/migration-report.html`
- `docs/demo/migration-report.md`
- `docs/index.html`
- `samples/LegacyShop/LegacyShop.Importador/LegacyShop.Importador.csproj`
- `samples/LegacyShop/LegacyShop.Importador/Program.cs`
- `samples/LegacyShop/LegacyShop.sln`
- `src/Migrator.Core/Analysis/ApplicationProfiler.cs`
- `src/Migrator.Core/Analysis/DataAccessAnalyzer.cs`
- `src/Migrator.Core/Cloud/CloudFormationGenerator.Ec2.cs`
- `src/Migrator.Core/Migration/LiteralExternalizer.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Framework.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.Settings.cs`
- `src/Migrator.Core/Migration/ProjectMigrator.cs`
- `src/Migrator.Core/Migration/SecretsExtractor.cs`
- `src/Migrator.Core/Reporting/HtmlReport.cs`
- `src/Migrator.Core/Reporting/MarkdownReport.cs`
- `src/Migrator.Core/Reporting/ReportWriter.cs`
- `tests/Migrator.Tests/FrameworkTargetTests.cs`
- `tests/Migrator.Tests/LlmTests.cs`
- `tests/Migrator.Tests/PortfolioTests.cs`
- `tests/Migrator.Tests/SampleSolutionTests.cs`
- `tests/Migrator.Tests/Snapshots/.github_workflows_deploy.yml.snap`
- `tests/Migrator.Tests/Snapshots/LegacyShop.Importador_LegacyShop.Importador.csproj.snap`
- `tests/Migrator.Tests/Snapshots/LegacyShop.Importador_Program.cs.snap`
- `tests/Migrator.Tests/Snapshots/LegacyShop.slnx.snap`
- `tests/Migrator.Tests/Snapshots/infra_terraform_ecs.tf.snap`

## Como aplicar no banco

1. Copie os arquivos **criados** e **alterados** para o mesmo caminho relativo no repositorio do banco (substituindo os existentes).
2. Exclua os **excluidos** e aplique os **renomeados** (apague o caminho antigo, crie o novo).
3. `dotnet build Migrator.slnx -warnaserror` e `dotnet test Migrator.slnx` devem passar (168 testes).
4. Preencha `DefaultEndpoint`/`DefaultTokenUrl` em `src/Migrator.Core/Llm/CorporateApiAssistant.cs` com o gateway do banco.
