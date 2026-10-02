# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 CLI (`migrator`) that reads a whole .NET Framework application (`.sln`/`.slnx`/`.csproj`/folder), writes a **migrated copy** targeting .NET 10 next to the original, builds that copy, and produces an **inventory** of what still needs manual work. It also produces **modernization advice** (libraries that went commercial or were discontinued, C# that compiles but behaves differently on .NET 10/Linux) and an **AWS target architecture** (hosting per project, managed services, Dockerfiles, Mermaid diagram, phases, risks, cost notes). The original source is never modified. It is built for a portfolio migration (dozens of apps, all going to AWS). All user-facing strings (inventory titles, suggestions, CLI help, report labels) are in **Brazilian Portuguese**; keep new rules and messages in pt-BR. Code identifiers and comments are in English.

The README is extensive and authoritative on behavior (pipeline steps, config mappings, package policies, known limitations). Read the relevant README section before changing a stage.

## Commands

```bash
dotnet build Migrator.slnx
dotnet test Migrator.slnx

# single test class / single test
dotnet test tests/Migrator.Tests --filter "FullyQualifiedName~CodeTransformerTests"
dotnet test tests/Migrator.Tests --filter "FullyQualifiedName~SampleSolutionTests.Analyze_inventories_the_whole_solution_without_writing_code"

# run the CLI against the bundled sample (offline = no nuget.org calls)
dotnet run --project src/Migrator.Cli -- analyze samples/LegacyShop/LegacyShop.sln --offline
dotnet run --project src/Migrator.Cli -- migrate samples/LegacyShop/LegacyShop.sln --offline --no-build --force

# with a local LLM (optional; Ollama must be running and the model pulled)
dotnet run --project src/Migrator.Cli -- migrate samples/LegacyShop/LegacyShop.sln --llm ollama --llm-model qwen2.5-coder:3b --force

# pack as a global tool
dotnet pack src/Migrator.Cli -o nupkg
```

Outputs under `samples/**/*.net10/` and `samples/**/*.migration-report/` are gitignored, so running the CLI on the sample is safe. `docs/` is the GitHub Pages site (served from `main`, folder `/docs`): `docs/index.html` is hand-written; `docs/demo/` holds the sample's report, regenerated with `analyze samples/LegacyShop/LegacyShop.sln --offline --report docs/demo` (delete the csv/xlsx afterwards; only the HTML and Markdown are published).

`ProjectFileWriter` normalizes every path attribute in the generated `.csproj` to MSBuild-style backslashes, so output (and the e2e tests) are identical on Windows, macOS and Linux.

## Architecture

Three projects: `Migrator.Core` (all logic), `Migrator.Cli` (thin System.CommandLine + Spectre.Console front end), `Migrator.Tests` (xUnit; has `InternalsVisibleTo` into Core).

### Pipeline (`Migration/MigrationEngine.RunAsync`)

`analyze` and `migrate` run the **same pipeline**; `analyze` is `DryRun = true` and simply never flushes the output plan or runs the build. Don't branch on DryRun inside per-project code.

1. `Analysis/WorkspaceLoader` resolves the input to a list of `.csproj` paths (non-C# projects become `SLN-SKIPPED` items). `Analysis/ProjectLoader` parses each into a `ProjectInfo` (old or SDK-style csproj, packages.config, references, items with metadata, project kind).
2. `Migration/ProjectMigrator.MigrateAsync` runs per project and returns a `MigratedProject` = `(ProjectResult, ProjectFileSpec?, OutputPlan)`. Nothing is written to disk here; every file goes into the `OutputPlan` as either a `Copy(source, relative)` or a `Write(relative, content)`.
3. `Migration/PackageAligner` orders projects topologically and bumps direct package versions across the solution to avoid NU1605, then the `.csproj` for each project is rendered via `ProjectFileWriter` into the plan.
4. Only when not DryRun: plans are applied to `OutputDir`, a `.slnx` plus root files (`NuGet.config`, `.editorconfig`, `Directory.Build.*`) are written, a `.migrator-output` marker is dropped (required for `--force` to ever delete the folder), and `BuildVerifier` runs `dotnet build` per project in dependency order. A project whose dependency failed is marked `BUILD-BLOCKED`, not built.
5. When `Options.Cloud == Aws` (default): `AdviseCloud` merges each project's `ApplicationProfile` with the profiles of everything it references (via `PackageAligner.Closure`), asks `Cloud/AwsArchitect.Recommend` for a `HostingRecommendation`, writes a `Dockerfile` into the plan of every deployable project (plus one root `.dockerignore`), and builds `SolutionResult.Architecture` with `AwsArchitect.Propose`.
5b. After a successful verification build (and the LLM loop), `VerifyRuntimeAsync` runs `RuntimeVerifier`: `dotnet test` on migrated test projects, a loopback start of each web app with `GET /health`, and `docker build` when `Options.VerifyDocker`. Results land in `ProjectResult.Tests/Smoke/DockerBuildSucceeded` and inventory items `TEST-*`, `SMOKE-*`, `DOCKER-*`; `ReportWriter.BuildLabel` folds them into the Build column.
5c. `SecretsExtractor` runs inside `ProjectMigrator` right after `ConfigMigrator`: credentials leave the generated appsettings (placeholder `<secret: name>`) and the artifacts go to `_secrets/<project>/` in the output root (git- and docker-ignored). `--keep-secrets` disables it.
5d. VB.NET projects (`ProjectInfo.IsVisualBasic`) are loaded by `WorkspaceLoader`/`ProjectLoader` like C# ones but `ProjectMigrator.AnalyzeVisualBasic` only profiles them (case-insensitive probes, `Signal.WebForms`) and emits `PRJ-VB`; `OutputProjectPath` stays null so they are neither written nor in the `.slnx`. Web Forms files in any web project are sized by `ReportWebForms` (`WEB-WEBFORMS`); a web project with only Web Forms is recommended `Ec2Windows`.
6. Optional LLM steps (`Llm/`), only when `Options.Llm.Provider != "none"` or an `ILlmAssistant` was passed to the `MigrationEngine` constructor: `LlmCodeDrafter` writes `*.Migrator.cs.txt` drafts after the plan is applied; after a failed verification build `FixBuildWithLlmAsync` runs `LlmCodeFixer` rounds (fix files → clear Build items → `VerifyBuildAsync` again → `SettleAsync` keeps/reverts); `LlmNarrator` fills `Architecture.ExecutiveSummary`. All calls go through `LlmSession.TryCompleteAsync`, which disables the LLM after the first infrastructure failure and adds one `LLM-UNAVAILABLE` warning. **The migrator must keep working with no LLM configured**; tests run with scripted assistants, never a real model.
7. `Reporting/ReportWriter.WriteAllAsync` emits HTML, Markdown, CSV (+ `modernization.csv`) and Excel (ClosedXML) from the same `SolutionResult`. The report classes are `partial`; the architecture/modernization sections live in `*.Cloud.cs` files.

### Per-project order inside `ProjectMigrator.MigrateAsync`

Classify items into roles (Code / LegacyCode / View / Static / Copy / LegacyMarkup / Config) → read text with `TextFiles.Read` (BOM-aware, falls back to Windows-1252) → `ControllerCatalog` (Roslyn, inheritance-aware) → `StartupAnalyzer` on legacy files (Global.asax, App_Start, OWIN Startup) producing a `StartupPlan` → `BundleConfigParser` → `ConfigMigrator` (web/app.config → appsettings*.json + `Hints` for Program.cs) → per `.cs` file: `ControllerRewriter` (Roslyn) then `CodeTransformer` (regex rewrites, yields `CodeFacts`) then `CodeRules.CSharp` detection → per `.cshtml`: `RazorTransformer` then `CodeRules.Razor` → `BuildRequirements` turns `CodeFacts` + config into `PackageRequirement`s → `PackagePlanner` (rules first, then nuget.org) → `BuildProjectSpec` → for web projects `ProgramGenerator` writes `Program.cs` and `launchSettings.json`.

The ordering matters: detection rules run on **already-transformed** code, so a rule's regex must match ASP.NET Core-shaped text (e.g. `configuration["AppSettings:..."]`, not `ConfigurationManager`). `CodeFacts` collected during transformation drive which packages get added and whether `App.config` is kept.

For `WindowsService` projects, `WorkerServiceRewriter.Discover` runs on the original sources; when it finds `ServiceBase` classes, `PrepareWorkerConversion` moves the service designer and the `ServiceBase.Run` Program.cs to `_Legacy/`, writes a generic-host Program.cs, and the per-file loop applies `WorkerServiceRewriter.Rewrite` between `ControllerRewriter` and `CodeTransformer`; `facts.ConvertedWindowsService` then switches the TFM to `net10.0` and the packages to `Microsoft.Extensions.Hosting(.WindowsServices)`, the `WindowsServiceHost` signal is dropped from the profile and `MOD-WIN-SERVICE` is removed. After `CodeTransformer`, `ConfigurationInjector.Inject` provides `IConfiguration configuration` in the `static Main` class (static `ConfigurationBuilder` field) and in `BackgroundService` classes (constructor injection, field initializers moved), for Console/WindowsService projects only. `Cloud/LambdaScaffolder` adds Function.cs + aws-lambda-tools-defaults.json + Amazon.Lambda packages to projects whose hosting is `Lambda`; `AdviseCloud` therefore runs before the `.csproj` files are rendered.

In contrast, `Analysis/ApplicationProfiler.Analyze` and `Cloud/ModernizationAdvisor.Analyze` run on the **original** code (Code + LegacyCode entries) plus the raw config XML, where `System.Messaging`, `SmtpClient`, `Session[...]`, GAC references etc. are still recognizable. GAC `<Reference>`s only count as a signal when the code mentions the namespace (old templates reference `System.EnterpriseServices`/`System.Management`/`System.Drawing` without using them; counting them would wrongly force Windows containers).

### Infrastructure as code

`Cloud/InfrastructureGenerator.Generate(result, profiles)` renders `infra/terraform/*.tf`, `infra/README.md` and `.github/workflows/deploy.yml` from the architecture (deployables = projects with a hosting other than NotDeployable/Desktop **and** an output project; VB and EC2 projects are listed as not generated). Secrets referenced in task definitions come from `ProjectResult.Secrets` (the extracted plan) as `data "aws_secretsmanager_secret"` lookups. `AlignHcl` mimics `terraform fmt` (consecutive attributes aligned; attributes opening a multi-line block excluded) so the output is fmt-clean; `MigrationEngine.WantsBom` writes .tf/.sh/.ps1/.yml/.md/Dockerfile without a BOM (a BOM breaks shebangs and fmt). The generated module for the sample passes `terraform fmt -check`, `init` and `validate`; re-check with those commands after changing the generator. `--no-infra` (`Options.GenerateInfrastructure`) disables it.

### Portfolio mode

`portfolio <folder>` (`Portfolio/PortfolioRunner`) discovers one application per `.sln`/`.slnx` (`.slnx` wins in the same folder; folders with only project files count as an app; Migrator outputs are skipped), runs `MigrationEngine` in dry-run for each into `<report>/apps/<name>/`, and `PortfolioAggregator` builds the consolidated view: `Score` (reference effort points, documented in the README), bands, inventory/modernization gaps by rule across apps, shared databases/internal hosts (`SolutionResult.Databases`/`InternalHosts`, filled in `AdviseCloud`), three waves (union-find on shared databases, cumulative effort), and `--baseline` deltas from a previous `portfolio.json`. `Reporting/JsonReport` writes `migration-result.json` for every run (DTOs, camelCase, enums as strings); `PortfolioReports` writes HTML/MD/XLSX/JSON.

### Rules are data

Almost all behavior changes are table entries in `src/Migrator.Core/Data/`:

- `PackageRules.ById` — `Keep` / `Replace` / `Remove` / `Manual` with a `VersionPolicy` (`SameIfCompatible`, `DotNet` = 10.0 line, `LatestMajor(n)`, `Latest`, `Fixed`; `MaxExclusive` encodes license ceilings like AutoMapper < 15). Packages with no rule are resolved live against nuget.org by `NuGet/NuGetClient` (lib/<tfm> folders decide compatibility).
- `CodeRules.CSharp` / `CodeRules.Razor` — regex detection rules (`CodeRule`) with optional `FileMustMatch` / `FileMustNotMatch` file filters.
- `FrameworkReferenceRules` — GAC `<Reference>` → package (only if the namespace is actually used in code), ignore, or report.
- `BuildHints` — maps compiler/restore diagnostic codes and missing type/member names to suggestions attached to build-error inventory items.

- `ModernizationRules.ById` / `ByPrefix` — package-level `PackageModernizationRule` (Kind License/Deprecated/Modernize/Cloud/Security, Impact, Effort, Proposal, optional `AwsService`). AWS-first: Azure packages map to AWS equivalents, MassTransit to AWS.Messaging, etc.
- `CodeModernizationRules.CSharp` — regex rules for C# that **compiles but changes behaviour** on .NET 10/Linux (code pages, `Encoding.Default`, `TransactionScope`/MSDTC, culture-dependent parse/format, backslash paths, `DateTime.Now` in UTC containers, `string.GetHashCode()`, sync-over-async, weak crypto...). Optional `OnlyKinds` restricts to project kinds; `FileMustMatch` is a file-level precondition.

The regex-based rewrites themselves live in `Migration/CodeTransformer.cs` (`NamespaceMap` plus rewrite tables) and `Migration/RazorTransformer.cs`.

### Modernization and cloud models

`ModernizationItem` (`Models/Modernization.cs`) is deliberately **not** an `InventoryItem`: it never counts against `AutomationPercent` or the exit code. Items come from three sources in `ModernizationAdvisor.Analyze`: package rules, code rules, and `FromProfile` (architectural signals such as MSMQ, local files, InProc session, Integrated Security, on-prem hosts). Rule-ID prefixes: `MOD-PKG-*`, `MOD-CS-*`, `MOD-ARCH-*`, `MOD-WIN-*` (Windows dependency with a Linux replacement), `MOD-SEC-*`, `MOD-COST-*`. Credentials are detected in two signals: `SecretsInConfig` (appSettings keys, connection strings, plus any attribute in the config whose name looks like a credential: SMTP `network/@password`, `identity/@password`, `sessionState/@sqlConnectionString`, fixed `machineKey`, custom sections) and `SecretsInCode` (C# literals with `Password=`, named key/token/secret literals, `AKIA...` access keys). Both feed `MOD-SEC-SECRETS*`, the Secrets Manager component and the hosting prerequisites; `CFG-SECRETS` from `ConfigMigrator` points to the same component.

`AwsArchitect` distinguishes **hard** Windows dependencies (COM, Registry, P/Invoke to Windows DLLs, Office Interop, Crystal/ReportViewer, WMI, IIS admin, WinForms/WPF → `EcsWindows`/`Ec2Windows`) from **soft** ones (System.Drawing, EventLog, PerformanceCounter, ServiceBase, MSMQ, Windows Auth, UNC → stay on `EcsFargate` with prerequisites). Workers become `Lambda` when event-driven (mailbox, files + spreadsheets, FileSystemWatcher, FTP or queue), free of Windows deps other than ServiceBase/EventLog, without an embedded scheduler (Quartz/Hangfire) and with ≤ `LambdaMaxSourceFiles` source files; otherwise `EcsFargateWorker` when queue-driven, `EcsScheduledTask` when timer/scheduler-driven. Lambda projects get no Dockerfile (item `AWS-LAMBDA`). Signals for back-office automations: `MailboxReading` (EWS/IMAP/POP3/Graph/Outlook), `FileWatcher`, `SpreadsheetFiles`, `OfficeOleDb` (ACE/Jet = hard Windows). Components `s3-events`, `storage-gateway`, `ses-inbound`, `lambda`. `Propose` builds components only from deployable projects (their merged profiles include libraries), so "used by" never lists a library or test project. Generated Dockerfiles assume `DefaultCulture = "pt-BR"` (TZ/LANG) when the code depends on local time and the config declares no culture.

### LLM layer (`Llm/`)

`ILlmAssistant` is intentionally minimal (system message + user message → text) so the corporate SDK can be plugged in by implementing it and either adding a case to `LlmAssistantFactory.Create` or passing the instance to `new MigrationEngine(assistant)`. `CachedLlmAssistant` keys responses by `Name` + prompts under `~/.migrator/llm-cache`; `OllamaAssistant` uses temperature 0 / fixed seed. Prompts live in `LlmPrompts` and are part of the cache key, so changing them invalidates cached answers. `LlmPrompts.ExtractCode` + `LooksLikeValidReplacement` gate every file written by the model; the compiler is the final oracle (`LlmCodeFixer.SettleAsync` reverts files whose error count did not drop, then the model gets one retry with those errors as feedback; `MaxAttemptsPerFile = 2`). Because C# reports method-body errors only after declaration errors elsewhere are gone, a file kept as fixed in round N is reverted to its `.before` backup if it shows errors again in round N+1 (`RevertLateRegressionAsync`), and the engine rebuilds after any revert so the report matches the disk. The user message includes the migrated project's package/framework list so the model does not invent dependencies. Inventory rule ids: `LLM-FIX`, `LLM-FIX-PARTIAL`, `LLM-FIX-REVERTED`, `LLM-FIX-FAILED`, `LLM-SUMMARY`, `LLM-DRAFT`, `LLM-UNAVAILABLE`.

### Inventory model (`Models/InventoryItem`)

Every stage reports into `ProjectResult.Inventory` or `SolutionResult.GlobalItems`. `AutoMigrated = true` means "done for you" (Info severity, shown as green/Automático). `RequiresAction` = not auto and not Info. `Severity.Breaking` items or a failed verification build make the CLI exit with code 2 (0 = clean, 1 = usage/IO error, 130 = cancelled).

Rule ID prefixes are a convention, not enforced: `WEB*`/`NET*`/`CFG*` and `CS-*` for C#, `VW*` for Razor, `PRJ-*` project file, `PKG-*` packages, `STARTUP-*`, `CFG-*` config, `SLN-*` solution-level, `BUILD-*` and raw diagnostic codes (`CS0246`, `NU1701`) for build results. Tests assert on rule IDs, so treat existing IDs as stable.

### Design decisions to preserve (from README "Decisões de projeto")

- Output is always a copy outside the source tree; `PrepareOutputDirectory` refuses nested paths and refuses to delete folders lacking the `.migrator-output` marker.
- Behavior-preserving over modern: Web API keeps Newtonsoft.Json and PascalCase, EF6 stays on 6.5.x, `Nullable`/`ImplicitUsings` are off in generated projects, `AssemblyInfo.cs` is kept, `Encrypt=False` is appended to SQL Server connection strings solution-wide.
- Don't invent code where it's risky: `ConfigurationManager` is rewritten to `configuration[...]` but injecting `IConfiguration` is left to the developer (the build error points to it). Same for `HttpContext.Current` outside controllers.

## Tests

- Unit tests call the static transformers directly (`CodeTransformer.Transform`, `ControllerRewriter.Rewrite`, `ConfigMigrator`, `StartupAnalyzer`, `RazorTransformer`) with inline C#/XML/Razor raw strings.
- `SampleSolutionTests` runs the full `MigrationEngine` against `samples/LegacyShop` (a deliberately messy MVC 5 + Web API 2 + Windows Service + scheduled console automation (EWS mailbox + network folder + CSV) + VB.NET solution) with `Offline = true, VerifyBuild = false`, into a temp dir. It locates the repo root by walking up to `Migrator.slnx`. It also asserts the expected hosting per project (Web → `EcsFargate`, Worker → `EcsScheduledTask`), key architecture component ids and modernization rule ids.
- `LlmTests` uses `ScriptedAssistant`/`BrokenAssistant` fakes and an `HttpMessageHandler` stub for `OllamaAssistant`; it also runs the engine on the sample with and without an assistant to prove the LLM layer is optional.
- `WorkerServiceTests`, `ConfigurationInjectorTests` and `SecretsTests` cover the deterministic worker conversion, the IConfiguration injection and the secrets extraction with inline sources.
- `ModernizationAndCloudTests` covers package/code rules, the profiler (config + code signals, EF6 entity connection strings, internal-host detection), hosting decisions (hard vs soft Windows deps, propagation through project references), Dockerfile generation and the cloud additions to `Program.cs`. Build profiles with `ApplicationProfiler.Analyze(project, [("File.cs", code)], XElement.Parse(config))`.
- When adding a rule: add a unit test, and if the pattern is common add a reproduction to `samples/LegacyShop` and assert its rule ID in `SampleSolutionTests`.
