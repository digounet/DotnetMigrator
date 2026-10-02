using System.Net;
using System.Text.Json.Nodes;
using Migrator.Core.Llm;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>The LLM layer is tested with scripted assistants: no model is needed to run the suite.</summary>
public sealed class LlmTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-llm-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private sealed class ScriptedAssistant(Func<string, string, string> answer, string name = "fake/test") : ILlmAssistant
    {
        public string Name => name;
        public int Calls { get; private set; }
        public List<string> UserMessages { get; } = [];

        public Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default)
        {
            Calls++;
            UserMessages.Add(userMessage);
            return Task.FromResult(answer(systemMessage, userMessage));
        }
    }

    private sealed class BrokenAssistant : ILlmAssistant
    {
        public string Name => "fake/broken";
        public Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("connection refused");
    }

    private static ProjectResult WebProject(string name, string relativeDir) => new()
    {
        Project = new ProjectInfo { ProjectPath = Path.Combine("C:", "src", name, name + ".csproj"), Name = name, Kind = ProjectKind.Web },
        RelativeDir = relativeDir
    };

    private static InventoryItem BuildError(string project, string file, int line, string code, string message, string suggestion) => new()
    {
        Project = project, Severity = InventorySeverity.Breaking, Category = InventoryCategory.Build, RuleId = code,
        Title = $"{code}: {message}", Description = message, Suggestion = suggestion, FilePath = file, Line = line
    };

    // ------------------------------------------------------------------ parsing / sanity

    [Fact]
    public void Extracts_the_largest_code_fence_and_rejects_prose()
    {
        const string response = "Aqui está:\n```csharp\nusing System;\npublic class A { }\n```\nE um exemplo menor:\n```\nvar x = 1;\n```\n";
        Assert.Equal("using System;\npublic class A { }", LlmPrompts.ExtractCode(response));
        Assert.Equal("using System;\nnamespace X { class B { } }", LlmPrompts.ExtractCode("using System;\nnamespace X { class B { } }"));
        Assert.Null(LlmPrompts.ExtractCode("Não consigo corrigir este arquivo."));
    }

    [Fact]
    public void Replacement_sanity_checks_catch_truncated_or_unbalanced_output()
    {
        var original = new string('x', 1000) + " class A { }";
        Assert.False(LlmPrompts.LooksLikeValidReplacement(original, "class A { }"));                 // too short
        Assert.False(LlmPrompts.LooksLikeValidReplacement("class A { }", "class A { "));                // unbalanced
        Assert.False(LlmPrompts.LooksLikeValidReplacement("class A { }", "// only a comment ....."));   // no type
        Assert.True(LlmPrompts.LooksLikeValidReplacement("class A { }", "using System;\nclass A { void M() { } }"));
    }

    [Fact]
    public void Ollama_request_is_deterministic_and_carries_both_messages()
    {
        var request = OllamaAssistant.BuildRequest("qwen2.5-coder:3b", "sys", "user");
        Assert.Equal("qwen2.5-coder:3b", (string)request["model"]!);
        Assert.False((bool)request["stream"]!);
        Assert.Equal(0, (int)request["options"]!["temperature"]!);
        var messages = (JsonArray)request["messages"]!;
        Assert.Equal(["system", "user"], messages.Select(m => (string)m!["role"]!));
        Assert.Equal("sys", (string)messages[0]!["content"]!);
    }

    [Fact]
    public async Task Ollama_assistant_parses_chat_response_and_reports_http_errors()
    {
        var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"model":"m","message":{"role":"assistant","content":"resposta"},"done":true}""")
        });
        using var ok = new OllamaAssistant("http://ollama.test", "m", handler: handler);
        Assert.Equal("resposta", await ok.CompleteAsync("s", "u"));
        Assert.Equal("ollama/m", ok.Name);
        Assert.Equal("http://ollama.test/api/chat", handler.LastRequest!.RequestUri!.ToString());

        using var missing = new OllamaAssistant("http://ollama.test", "nope", handler: new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"model 'nope' not found"}""") }));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.CompleteAsync("s", "u"));
        Assert.Contains("ollama pull nope", ex.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    // ------------------------------------------------------------------ cache / factory / session

    [Fact]
    public async Task Cache_returns_the_same_answer_without_calling_the_model_again()
    {
        var inner = new ScriptedAssistant((_, u) => "resposta para " + u);
        var cached = new CachedLlmAssistant(inner, Path.Combine(_work, "cache"));
        Assert.Equal("resposta para a", await cached.CompleteAsync("s", "a"));
        Assert.Equal("resposta para a", await cached.CompleteAsync("s", "a"));
        Assert.Equal("resposta para b", await cached.CompleteAsync("s", "b"));
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, cached.Hits);
    }

    [Fact]
    public void Factory_disables_by_default_and_rejects_unknown_providers()
    {
        Assert.Null(LlmAssistantFactory.Create(new LlmOptions()));
        Assert.False(new LlmOptions().Enabled);
        var ollama = LlmAssistantFactory.Create(new LlmOptions { Provider = "ollama", CacheDir = null });
        Assert.IsType<OllamaAssistant>(ollama);
        Assert.Equal("ollama/qwen2.5-coder:3b", ollama!.Name);
        Assert.IsType<CachedLlmAssistant>(LlmAssistantFactory.Create(new LlmOptions { Provider = "ollama", CacheDir = _work }));
        var ex = Assert.Throws<NotSupportedException>(() => LlmAssistantFactory.Create(new LlmOptions { Provider = "sdk-empresa" }));
        Assert.Contains("ILlmAssistant", ex.Message);
    }

    [Fact]
    public async Task Session_stops_after_the_first_failure_and_records_one_warning()
    {
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work };
        var session = new LlmSession(new BrokenAssistant(), result);
        Assert.Null(await session.TryCompleteAsync("s", "u", CancellationToken.None));
        Assert.Null(await session.TryCompleteAsync("s", "u", CancellationToken.None));
        Assert.False(session.Available);
        Assert.Equal(1, session.Calls);
        Assert.Single(result.GlobalItems, i => i.RuleId == "LLM-UNAVAILABLE" && i.Severity == InventorySeverity.Warning);
    }

    // ------------------------------------------------------------------ fixer

    [Fact]
    public async Task Fixer_rewrites_files_with_errors_keeps_a_backup_and_settles_outcomes()
    {
        var output = Path.Combine(_work, "out");
        var report = Path.Combine(output, "_migration-report");
        Directory.CreateDirectory(Path.Combine(output, "Core", "Services"));
        var servicePath = Path.Combine(output, "Core", "Services", "ProdutoService.cs");
        const string original = """
            public class ProdutoService
            {
                private readonly string _cs = configuration.GetConnectionString("Default");
            }
            """;
        await File.WriteAllTextAsync(servicePath, original);

        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = report };
        var core = WebProject("Core", "Core");
        core.Inventory.Add(BuildError("Core", "Services/ProdutoService.cs", 3, "CS0103", "The name 'configuration' does not exist in the current context", "Injete IConfiguration no construtor."));
        core.Inventory.Add(BuildError("Core", "_Legacy/Old.cs", 1, "CS0103", "ignored", ""));
        result.Projects.Add(core);

        const string fixedCode = """
            using Microsoft.Extensions.Configuration;
            public class ProdutoService
            {
                private readonly string _cs;
                public ProdutoService(IConfiguration configuration) { _cs = configuration.GetConnectionString("Default"); }
            }
            """;
        var assistant = new ScriptedAssistant((_, _) => "```csharp\n" + fixedCode + "\n```");
        var session = new LlmSession(assistant, result);
        var fixer = new LlmCodeFixer(session, new LlmOptions { Provider = "fake" }, output, report);

        var attempts = await fixer.FixRoundAsync(result, null, CancellationToken.None);

        Assert.Single(attempts);
        Assert.Equal("Services/ProdutoService.cs", attempts[0].RelativeFile);
        Assert.Contains("linha 3: CS0103", assistant.UserMessages[0]);
        Assert.Contains("dica: Injete IConfiguration", assistant.UserMessages[0]);
        Assert.Contains("IConfiguration configuration", await File.ReadAllTextAsync(servicePath));
        Assert.Equal(original, await File.ReadAllTextAsync(attempts[0].BackupPath));

        // Simulate the rebuild: no more errors in the file, project builds
        core.Inventory.RemoveAll(i => i.Category == InventoryCategory.Build);
        core.Build = new ProjectBuildStatus(0, 0);
        await fixer.SettleAsync(attempts, result, CancellationToken.None);
        Assert.Equal(1, fixer.FilesFixed);
        var item = Assert.Single(core.Inventory, i => i.RuleId == "LLM-FIX");
        Assert.True(item.AutoMigrated);
        Assert.Contains("CS0103", item.Title);
    }

    [Fact]
    public async Task Fixer_reverts_a_change_that_does_not_reduce_errors_and_does_not_retry_it()
    {
        var output = Path.Combine(_work, "out2");
        var report = Path.Combine(output, "_migration-report");
        Directory.CreateDirectory(Path.Combine(output, "Web"));
        var path = Path.Combine(output, "Web", "A.cs");
        const string original = "public class A { void M() { var x = Oops(); } }";
        await File.WriteAllTextAsync(path, original);

        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = report };
        var web = WebProject("Web", "Web");
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0103", "The name 'Oops' does not exist", ""));
        result.Projects.Add(web);

        var assistant = new ScriptedAssistant((_, _) => "```csharp\npublic class A { void M() { var x = Oops(); var y = StillBroken(); } }\n```");
        var fixer = new LlmCodeFixer(new LlmSession(assistant, result), new LlmOptions { Provider = "fake" }, output, report);
        var attempts = await fixer.FixRoundAsync(result, null, CancellationToken.None);
        Assert.Single(attempts);

        // Rebuild "found" two errors now
        web.Inventory.RemoveAll(i => i.Category == InventoryCategory.Build);
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0103", "The name 'Oops' does not exist", ""));
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0103", "The name 'StillBroken' does not exist", ""));
        web.Build = new ProjectBuildStatus(2, 0);
        await fixer.SettleAsync(attempts, result, CancellationToken.None);

        Assert.Equal(1, fixer.FilesReverted);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Contains(web.Inventory, i => i.RuleId == "LLM-FIX-REVERTED");
        Assert.True(File.Exists(Path.ChangeExtension(attempts[0].BackupPath, ".llm.cs.txt")));

        Assert.True(fixer.NeedsRebuild);

        // Second attempt: the model sees the errors its first proposal produced
        var second = await fixer.FixRoundAsync(result, null, CancellationToken.None);
        Assert.Single(second);
        Assert.Equal(2, assistant.Calls);
        Assert.Contains("Tentativa anterior rejeitada", assistant.UserMessages[1]);
        Assert.Contains("StillBroken", assistant.UserMessages[1]);

        // Still bad → reverted again, and now the file is left alone
        web.Inventory.RemoveAll(i => i.Category == InventoryCategory.Build && !i.RuleId.StartsWith("LLM-"));
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0103", "The name 'Oops' does not exist", ""));
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0103", "The name 'StillBroken' does not exist", ""));
        web.Build = new ProjectBuildStatus(2, 0);
        await fixer.SettleAsync(second, result, CancellationToken.None);
        Assert.Equal(2, fixer.FilesReverted);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(await fixer.FixRoundAsync(result, null, CancellationToken.None));
        Assert.Equal(2, assistant.Calls);
        Assert.Equal(2, fixer.Round);
    }

    [Fact]
    public async Task Fixer_tells_the_model_which_packages_the_project_has()
    {
        var output = Path.Combine(_work, "out5");
        Directory.CreateDirectory(Path.Combine(output, "Core"));
        await File.WriteAllTextAsync(Path.Combine(output, "Core", "A.cs"), "public class A { }");
        await File.WriteAllTextAsync(Path.Combine(output, "Core", "Core.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Dapper" Version="2.1.66" /><PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" /></ItemGroup>
            </Project>
            """);
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = Path.Combine(output, "rep") };
        var core = new ProjectResult { Project = new ProjectInfo { ProjectPath = Path.Combine("C:", "src", "Core", "Core.csproj"), Name = "Core", Kind = ProjectKind.ClassLibrary }, RelativeDir = "Core", OutputProjectPath = Path.Combine("Core", "Core.csproj") };
        core.Inventory.Add(BuildError("Core", "A.cs", 1, "CS0103", "x", ""));
        result.Projects.Add(core);

        var assistant = new ScriptedAssistant((_, _) => "nada");
        await new LlmCodeFixer(new LlmSession(assistant, result), new LlmOptions { Provider = "fake" }, output, result.ReportDir!).FixRoundAsync(result, null, CancellationToken.None);
        Assert.Contains("Pacotes NuGet: Dapper 2.1.66, Microsoft.Extensions.Configuration.Abstractions 10.0.0", assistant.UserMessages[0]);
        Assert.Contains("sem ASP.NET Core", assistant.UserMessages[0]);
    }

    [Fact]
    public async Task Fixer_reverts_an_earlier_fix_whose_errors_only_surfaced_later()
    {
        var output = Path.Combine(_work, "out6");
        var report = Path.Combine(output, "rep");
        Directory.CreateDirectory(Path.Combine(output, "Core"));
        var path = Path.Combine(output, "Core", "Cache.cs");
        const string original = "public class Cache { void M() { var f = new BinaryFormatter(); } }";
        await File.WriteAllTextAsync(path, original);
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = report };
        var core = WebProject("Core", "Core");
        core.Inventory.Add(BuildError("Core", "Cache.cs", 1, "SYSLIB0011", "BinaryFormatter is obsolete", ""));
        result.Projects.Add(core);

        var answers = new Queue<string>(["```csharp\npublic class Cache { void M() { var x = Json.Clone(1); } }\n```", "```csharp\npublic class Cache { void M() { } }\n```"]);
        var assistant = new ScriptedAssistant((_, _) => answers.Dequeue());
        var fixer = new LlmCodeFixer(new LlmSession(assistant, result), new LlmOptions { Provider = "fake" }, output, report);

        // Round 1: compiler reports nothing in this file (declaration errors elsewhere hide the body error) → kept as fixed
        var first = await fixer.FixRoundAsync(result, null, CancellationToken.None);
        core.Inventory.RemoveAll(i => i.Category == InventoryCategory.Build);
        core.Build = new ProjectBuildStatus(0, 0);
        await fixer.SettleAsync(first, result, CancellationToken.None);
        Assert.Equal(1, fixer.FilesFixed);

        // Round 2: now the body error surfaces (2 errors ≥ the original's 1) → original restored, model retried with feedback
        core.Inventory.Add(BuildError("Core", "Cache.cs", 1, "CS0103", "The name 'Json' does not exist", ""));
        core.Inventory.Add(BuildError("Core", "Cache.cs", 1, "CS0103", "x", ""));
        core.Build = new ProjectBuildStatus(2, 0);
        var second = await fixer.FixRoundAsync(result, null, CancellationToken.None);
        Assert.Single(second);
        Assert.Equal(0, fixer.FilesFixed);
        Assert.Equal(1, fixer.FilesReverted);
        Assert.Contains("Tentativa anterior rejeitada", assistant.UserMessages[1]);
        Assert.Contains("'Json' does not exist", assistant.UserMessages[1]);
        Assert.Equal(original, await File.ReadAllTextAsync(second[0].BackupPath));
        Assert.DoesNotContain(core.Inventory, i => i.RuleId == "LLM-FIX");
        Assert.Contains(core.Inventory, i => i.RuleId == "LLM-FIX-REVERTED" && i.Title.Contains("após os demais arquivos compilarem"));
        Assert.Contains("void M() { }", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Fixer_records_a_failure_when_the_model_answers_with_prose()
    {
        var output = Path.Combine(_work, "out3");
        Directory.CreateDirectory(Path.Combine(output, "Web"));
        await File.WriteAllTextAsync(Path.Combine(output, "Web", "A.cs"), "public class A { }");
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = Path.Combine(output, "rep") };
        var web = WebProject("Web", "Web");
        web.Inventory.Add(BuildError("Web", "A.cs", 1, "CS0246", "x", ""));
        result.Projects.Add(web);

        var fixer = new LlmCodeFixer(new LlmSession(new ScriptedAssistant((_, _) => "Desculpe, não sei."), result), new LlmOptions { Provider = "fake" }, output, result.ReportDir!);
        var attempts = await fixer.FixRoundAsync(result, null, CancellationToken.None);
        Assert.Empty(attempts);
        Assert.Contains(web.Inventory, i => i.RuleId == "LLM-FIX-FAILED");
        Assert.Equal("public class A { }", await File.ReadAllTextAsync(Path.Combine(output, "Web", "A.cs")));
        Assert.True(File.Exists(Path.Combine(result.ReportDir!, "llm", "rodada-1", "Web", "A.cs.resposta-rejeitada.txt")));
    }

    // ------------------------------------------------------------------ drafter / narrator

    [Fact]
    public async Task Drafter_writes_a_txt_next_to_legacy_files_and_never_touches_them()
    {
        var output = Path.Combine(_work, "out4");
        Directory.CreateDirectory(Path.Combine(output, "Web", "Modules"));
        var module = Path.Combine(output, "Web", "Modules", "RequestTimingModule.cs");
        await File.WriteAllTextAsync(module, "public class RequestTimingModule : IHttpModule { }");
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, OutputDir = output, ReportDir = Path.Combine(output, "rep") };
        var web = WebProject("Web", "Web");
        web.Inventory.Add(new InventoryItem { Project = "Web", RuleId = "WEB016", Severity = InventorySeverity.Breaking, Category = InventoryCategory.Code, Title = "IHttpModule", FilePath = "Modules/RequestTimingModule.cs" });
        web.Inventory.Add(new InventoryItem { Project = "Web", RuleId = "WEB003", Severity = InventorySeverity.Breaking, Category = InventoryCategory.Code, Title = "Session", FilePath = "Controllers/X.cs" });
        result.Projects.Add(web);

        var drafter = new LlmCodeDrafter(new LlmSession(new ScriptedAssistant((_, _) => "```csharp\n// app.UseMiddleware<RequestTimingMiddleware>();\npublic class RequestTimingMiddleware { }\n```"), result), new LlmOptions { Provider = "fake" }, output);
        await drafter.DraftAsync(result, null, CancellationToken.None);

        Assert.Equal(1, drafter.Drafts);
        var draft = Path.Combine(output, "Web", "Modules", "RequestTimingModule.Migrator.cs.txt");
        Assert.True(File.Exists(draft));
        Assert.Contains("RequestTimingMiddleware", await File.ReadAllTextAsync(draft));
        Assert.Equal("public class RequestTimingModule : IHttpModule { }", await File.ReadAllTextAsync(module));
        var item = Assert.Single(web.Inventory, i => i.RuleId == "LLM-DRAFT");
        Assert.Equal("Modules/RequestTimingModule.Migrator.cs.txt", item.FilePath);
    }

    [Fact]
    public async Task Narrator_feeds_the_dossier_and_stores_the_executive_summary()
    {
        var result = new SolutionResult { Options = new MigrationOptions { InputPath = "x" }, RootDir = _work, SolutionName = "Loja" };
        var web = WebProject("Loja.Web", "Loja.Web");
        web.Hosting = new HostingRecommendation { Project = "Loja.Web", Kind = ProjectKind.Web, Primary = AwsHosting.EcsFargate };
        web.Hosting.Rationale.Add("sem dependências Windows");
        result.Projects.Add(web);
        result.Architecture = new ArchitectureProposal { Summary = "Loja tem 1 projeto." };
        result.Architecture.Components.Add(new AwsComponent { Id = "ecs", Service = "Amazon ECS", Role = "containers" });
        var profile = new Migrator.Core.Analysis.ApplicationProfile { Project = "Loja.Web", Kind = ProjectKind.Web };
        profile.Add(Migrator.Core.Analysis.Signal.InProcSession, "Controllers/Home.cs:10", "sessionState mode=InProc");

        var assistant = new ScriptedAssistant((_, _) => "**Resumo executivo**\n\nLoja.Web é um site de vendas.\n\n- risco 1");
        await LlmNarrator.NarrateAsync(new LlmSession(assistant, result), result, [(web, profile)], null, CancellationToken.None);

        Assert.Contains("Loja.Web é um site de vendas.", result.Architecture.ExecutiveSummary);
        Assert.Equal("fake/test", result.Architecture.ExecutiveSummaryModel);
        Assert.Contains("InProcSession", assistant.UserMessages[0]);
        Assert.Contains("ECS Fargate (Linux) + ALB", assistant.UserMessages[0]);
        Assert.Contains("Amazon ECS", assistant.UserMessages[0]);
    }

    // ------------------------------------------------------------------ engine integration (no LLM configured keeps working)

    [Fact]
    public async Task Engine_without_llm_has_no_llm_items_and_a_custom_assistant_is_used_when_passed()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        var sample = Path.Combine(dir!.FullName, "samples", "LegacyShop", "LegacyShop.sln");

        var plain = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = sample, DryRun = true, Offline = true, ReportDir = Path.Combine(_work, "r1") });
        Assert.Null(plain.LlmModel);
        Assert.DoesNotContain(plain.AllItems, i => i.RuleId.StartsWith("LLM-"));
        Assert.Null(plain.Architecture!.ExecutiveSummary);

        var assistant = new ScriptedAssistant((_, _) => "Resumo escrito pela LLM.", "empresa/sdk");
        var assisted = await new MigrationEngine(assistant).RunAsync(new MigrationOptions
        {
            InputPath = sample, DryRun = true, Offline = true, ReportDir = Path.Combine(_work, "r2"), Llm = new LlmOptions { CacheDir = null }
        });
        Assert.Equal("empresa/sdk", assisted.LlmModel);
        Assert.Equal("Resumo escrito pela LLM.", assisted.Architecture!.ExecutiveSummary);
        Assert.Equal(1, assistant.Calls); // analyze: only the narrative (no build, no files on disk)
        Assert.Contains("Leitura do arquiteto (LLM: empresa/sdk)", await File.ReadAllTextAsync(Path.Combine(_work, "r2", "migration-report.md")));

        var broken = await new MigrationEngine(new BrokenAssistant()).RunAsync(new MigrationOptions
        {
            InputPath = sample, DryRun = true, Offline = true, ReportDir = Path.Combine(_work, "r3"), Llm = new LlmOptions { CacheDir = null }
        });
        Assert.Contains(broken.GlobalItems, i => i.RuleId == "LLM-UNAVAILABLE");
        Assert.Equal(5, broken.Projects.Count); // migration itself unaffected
    }
}
