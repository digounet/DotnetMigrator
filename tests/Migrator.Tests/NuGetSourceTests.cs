using System.Net;
using Migrator.Core.Migration;
using Migrator.Core.Models;
using Migrator.Core.NuGet;
using NuGet.Versioning;

namespace Migrator.Tests;

public sealed class NuGetSourceTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-nuget-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public void Parses_private_feed_sources_credentials_and_disabled_entries()
    {
        var path = Path.Combine(_work, "nuget.config");
        Environment.SetEnvironmentVariable("ARTIFACTORY_TOKEN", "tok-123");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="artifactory remote" value="https://artifactory.empresa.com.br/artifactory/api/nuget/v3/nuget-remote/index.json" />
                <add key="legacy v2" value="https://artifactory.empresa.com.br/artifactory/api/nuget/nuget-v2" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="local" value="C:\pacotes" />
              </packageSources>
              <disabledPackageSources>
                <add key="nuget.org" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <artifactory_x0020_remote>
                  <add key="Username" value="svc-migrator" />
                  <add key="ClearTextPassword" value="%ARTIFACTORY_TOKEN%" />
                </artifactory_x0020_remote>
              </packageSourceCredentials>
            </configuration>
            """);

        var sources = NuGetConfigFile.Parse(path);
        Assert.Equal(["artifactory remote", "legacy v2"], sources.Select(s => s.Name));   // nuget.org disabled, local path ignored
        var primary = NuGetConfigFile.Primary(path);
        Assert.Equal("artifactory remote", primary.Name);
        Assert.False(primary.IsNuGetOrg);
        Assert.Equal(("svc-migrator", "tok-123"), (primary.UserName, primary.Password)); // %VAR% expanded
        Assert.True(primary.HasCredentials);
        Assert.Equal(path, NuGetConfigFile.Find(_work));
    }

    [Fact]
    public async Task Client_discovers_resources_from_the_private_service_index_and_sends_basic_auth()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            string body = url switch
            {
                "https://feed.test/v3/index.json" => """{"resources":[{"@id":"https://feed.test/v3/flat/","@type":"PackageBaseAddress/3.0.0"},{"@id":"https://feed.test/v3/reg/","@type":"RegistrationsBaseUrl/3.6.0"},{"@id":"https://feed.test/v3/reg-old/","@type":"RegistrationsBaseUrl"}]}""",
                "https://feed.test/v3/flat/automapper/index.json" => """{"versions":["12.0.1","14.0.0","15.0.1"]}""",
                _ => ""
            };
            return body.Length == 0 ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var client = new NuGetClient(false, new NuGetSource("feed", "https://feed.test/v3/index.json", "user", "secret"), handler);

        var versions = await client.GetVersionsAsync("AutoMapper");
        Assert.Equal(LookupStatus.Found, versions.Status);
        Assert.Equal(NuGetVersion.Parse("15.0.1"), versions.Latest());
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString() == "https://feed.test/v3/index.json");
        Assert.All(handler.Requests, r => Assert.Equal("Basic", r.Headers.Authorization!.Scheme));
        Assert.True(client.IsOnline);
    }

    [Fact]
    public async Task Client_goes_offline_when_the_private_index_is_unusable_instead_of_hitting_nuget_org()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = new NuGetClient(false, new NuGetSource("feed", "https://feed.test/v3/index.json"), handler);
        var versions = await client.GetVersionsAsync("AutoMapper");
        Assert.NotEqual(LookupStatus.Found, versions.Status);
        Assert.False(client.IsOnline);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host.Contains("nuget.org"));
    }

    [Fact]
    public async Task Probe_reports_reachability_quickly_with_a_useful_detail()
    {
        var ok = await NuGetClient.ProbeAsync(new NuGetSource("feed", "https://feed.test/v3/index.json"), TimeSpan.FromSeconds(5), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.True(ok.Ok);
        var forbidden = await NuGetClient.ProbeAsync(new NuGetSource("feed", "https://feed.test/v3/index.json"), TimeSpan.FromSeconds(5), new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.False(forbidden.Ok);
        Assert.Contains("credenciais", forbidden.Detail);
        var down = await NuGetClient.ProbeAsync(new NuGetSource("feed", "https://feed.test/v3/index.json"), TimeSpan.FromSeconds(5), new StubHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.False(down.Ok);
        Assert.Contains("connection refused", down.Detail);
    }

    [Theory]
    [InlineData("net48;netstandard2.0", "net10.0")]
    [InlineData("netcoreapp3.1", "net10.0")]
    [InlineData("net6.0-windows", "net10.0-windows")]
    [InlineData("net8.0;net8.0-windows10.0.19041.0", "net10.0;net10.0-windows10.0.19041.0")]
    [InlineData("netstandard2.1", "net10.0")]
    [InlineData("net10.0", "net10.0")]
    [InlineData("net472", "net10.0")]
    [InlineData("$(MyTfm)", "$(MyTfm)")]
    public void Upgrades_target_frameworks_to_net10(string before, string after) => Assert.Equal(after, ModernProjectUpdater.Upgrade(before));

    [Fact]
    public async Task Already_modern_sdk_project_is_rewritten_to_net10_and_microsoft_packages_follow_the_10_line()
    {
        var dir = Path.Combine(_work, "Lib");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "Lib.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net48;net6.0</TargetFrameworks>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="6.0.0" />
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), "namespace Lib { public class Class1 { } }");
        var project = Migrator.Core.Analysis.ProjectLoader.Load(csproj, _work);
        Assert.True(project.IsAlreadyModern);

        using var nuget = new NuGetClient(offline: true);
        var (text, before, after, updated, _) = await ModernProjectUpdater.UpdateAsync(project, new PackagePlanner(nuget));

        Assert.Equal(("net48;net6.0", "net10.0"), (before, after));
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", text);
        Assert.DoesNotContain("net48", text);
        Assert.Contains("<Nullable>enable</Nullable>", text);                                  // everything else preserved
        Assert.Contains("Include=\"Microsoft.Extensions.Logging.Abstractions\" Version=\"10.", text); // DotNet policy, even offline
        Assert.Contains("Include=\"Newtonsoft.Json\" Version=\"13.0.3\"", text);                 // non-Microsoft package untouched offline
        Assert.Equal(1, updated);

        // end to end through the engine: the copy on disk is the rewritten one
        var output = Path.Combine(_work, "out");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = csproj, OutputDir = output, Offline = true, VerifyBuild = false, Cloud = CloudTarget.None });
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", File.ReadAllText(Path.Combine(output, "Lib.csproj")));
        Assert.Contains(result.Projects[0].Inventory, i => i.RuleId == "PRJ-MODERN" && i.Title.Contains("net48;net6.0 → net10.0"));
    }

    [Fact]
    public async Task Explicit_nuget_config_is_copied_to_the_output_root_and_an_unreachable_feed_skips_the_build()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        var sample = Path.Combine(dir!.FullName, "samples", "LegacyShop", "LegacyShop.sln");
        var config = Path.Combine(_work, "artifactory.nuget.config");
        File.WriteAllText(config, """
            <configuration>
              <packageSources>
                <clear />
                <add key="artifactory" value="http://127.0.0.1:9/artifactory/api/nuget/v3/remote/index.json" />
              </packageSources>
            </configuration>
            """);
        var output = Path.Combine(_work, "out2");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = sample, OutputDir = output, Offline = true, VerifyBuild = true, BuildTimeout = TimeSpan.FromMinutes(1), NuGetConfigPath = config, Cloud = CloudTarget.None, GenerateInfrastructure = false
        });

        Assert.Equal("artifactory (http://127.0.0.1:9/artifactory/api/nuget/v3/remote/index.json)", result.NuGetSource);
        Assert.True(File.Exists(Path.Combine(output, "nuget.config")));
        Assert.Contains("api/nuget/v3/remote", File.ReadAllText(Path.Combine(output, "nuget.config")));
        Assert.Contains(result.GlobalItems, i => i.RuleId == "NUGET-CONFIG" && i.AutoMigrated);
        Assert.Contains(result.GlobalItems, i => i.RuleId == "BUILD-NUGET-UNREACHABLE" && i.Severity == InventorySeverity.Breaking);
        Assert.Equal("feed NuGet inacessível", result.BuildSkippedReason);
        Assert.Null(result.BuildSucceeded);
        Assert.All(result.Projects, p => Assert.Null(p.Build));          // no restore was even attempted
    }
}
