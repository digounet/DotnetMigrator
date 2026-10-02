using Migrator.Core.Migration;

namespace Migrator.Tests;

public class RuntimeVerifierTests
{
    [Fact]
    public void Parses_dotnet_test_summary_and_failed_test_names()
    {
        const string output = """
            [xUnit.net 00:00:00.10]     LegacyShop.Tests.CacheServiceTests.Clonar_preserva_valores [FAIL]
              Failed LegacyShop.Tests.CacheServiceTests.Clonar_preserva_valores [12 ms]
              Error Message:
               Assert.Equal() Failure
            Failed!  - Failed:     1, Passed:     7, Skipped:     2, Total:    10, Duration: 120 ms - LegacyShop.Tests.dll (net10.0)
            """;
        var status = RuntimeVerifier.ParseTestOutput(output);
        Assert.Equal((1, 7, 2, 10), (status.Failed, status.Passed, status.Skipped, status.Total));
        Assert.False(status.Succeeded);
        Assert.Equal(["LegacyShop.Tests.CacheServiceTests.Clonar_preserva_valores"], status.FailedTests);

        var ok = RuntimeVerifier.ParseTestOutput("Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 447 ms - X.dll (net10.0)");
        Assert.True(ok.Succeeded);
        Assert.Equal(45, ok.Passed);

        var none = RuntimeVerifier.ParseTestOutput("No test is available in X.dll");
        Assert.False(none.Succeeded);
        Assert.Equal(0, none.Total);
    }

    [Fact]
    public async Task Smoke_test_reports_a_missing_assembly_instead_of_throwing()
    {
        var status = await RuntimeVerifier.SmokeTestWebAsync(Path.Combine(Path.GetTempPath(), "nao-existe", "App.dll"), TimeSpan.FromSeconds(5));
        Assert.False(status.Succeeded);
        Assert.Contains("Assembly não encontrado", status.Detail);
    }

    [Fact]
    public async Task Process_runner_captures_output_and_exit_code()
    {
        var result = await RuntimeVerifier.RunAsync("dotnet", ["--version"], Path.GetTempPath(), TimeSpan.FromMinutes(1), null, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Matches(@"\d+\.\d+\.\d+", result.Output);
    }

    [Fact]
    public void Tail_keeps_the_last_lines_only()
    {
        var text = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"linha {i}"));
        var tail = RuntimeVerifier.Tail(text, 3);
        Assert.Equal("linha 28 | linha 29 | linha 30", tail);
    }
}
