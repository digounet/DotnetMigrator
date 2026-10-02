using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Migrator.Core.Migration;

public sealed record TestRunStatus(int Passed, int Failed, int Skipped, IReadOnlyList<string> FailedTests, bool TimedOut)
{
    public int Total => Passed + Failed + Skipped;
    public bool Succeeded => Failed == 0 && !TimedOut && Total > 0;
}

public sealed record SmokeTestStatus(bool Succeeded, int? StatusCode, string? Url, string Detail);

public sealed record ProcessResult(int ExitCode, bool TimedOut, string Output);

/// <summary>
/// "Compiles" is not "works": runs the migrated test projects, starts migrated web apps and probes /health,
/// and (opt-in) builds the generated Dockerfiles. All steps are best effort with timeouts; failures become inventory items.
/// </summary>
public static partial class RuntimeVerifier
{
    public static async Task<TestRunStatus> RunTestsAsync(string projectPath, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("dotnet", ["test", projectPath, "--no-build", "-nologo", "-v:q", "-tl:off", "-clp:NoSummary"], workingDirectory, timeout, null, cancellationToken);
        return ParseTestOutput(result.Output, result.TimedOut);
    }

    public static TestRunStatus ParseTestOutput(string output, bool timedOut = false)
    {
        int passed = 0, failed = 0, skipped = 0;
        var failedTests = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var summary = Summary().Match(line);
            if (summary.Success)
            {
                failed += int.Parse(summary.Groups["failed"].Value);
                passed += int.Parse(summary.Groups["passed"].Value);
                skipped += int.Parse(summary.Groups["skipped"].Value);
                continue;
            }
            var failedTest = FailedTest().Match(line);
            if (failedTest.Success && failedTests.Count < 20) failedTests.Add(failedTest.Groups["name"].Value.Trim());
        }
        return new TestRunStatus(passed, failed, skipped, failedTests, timedOut);
    }

    /// <summary>Starts the published web app on a random loopback port and requests /health.</summary>
    public static async Task<SmokeTestStatus> SmokeTestWebAsync(string assemblyPath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(assemblyPath)) return new SmokeTestStatus(false, null, null, $"Assembly não encontrado: {assemblyPath}. O build de verificação precisa ter gerado bin/Debug/net10.0.");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(assemblyPath);
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_ENVIRONMENT"] = "Development";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = new Process { StartInfo = psi };
        var log = new StringBuilder();
        var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (log) log.AppendLine(line);
            var m = Listening().Match(line);
            if (m.Success) listening.TrySetResult(m.Groups["url"].Value);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var exited = process.WaitForExitAsync(cancellationToken);
            var finished = await Task.WhenAny(listening.Task, exited, Task.Delay(timeout, cancellationToken));
            if (finished != listening.Task)
            {
                var reason = finished == exited ? $"o processo encerrou com código {process.ExitCode} antes de escutar" : $"não começou a escutar em {timeout.TotalSeconds:0}s";
                return new SmokeTestStatus(false, null, null, $"A aplicação {reason}. Saída: {Tail(log.ToString())}");
            }

            var url = listening.Task.Result.TrimEnd('/') + "/health";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var ok = response.IsSuccessStatusCode;
            return new SmokeTestStatus(ok, (int)response.StatusCode, url, ok ? $"GET /health → {(int)response.StatusCode} {body.Trim()}" : $"GET /health → {(int)response.StatusCode}. Saída: {Tail(log.ToString())}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return new SmokeTestStatus(false, null, null, $"{ex.GetType().Name}: {ex.Message}. Saída: {Tail(log.ToString())}");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    public static bool DockerAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public static Task<ProcessResult> DockerBuildAsync(string dockerfile, string contextDir, string tag, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        RunAsync("docker", ["build", "-f", dockerfile, "-t", tag, "."], contextDir, timeout, new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" }, cancellationToken);

    public static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        if (environment != null) foreach (var (k, v) in environment) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi };
        var log = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        process.WaitForExit();
        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, log.ToString());
    }

    public static string Tail(string text, int lines = 12)
    {
        var all = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", all.TakeLast(lines).Select(l => l.Trim()));
    }

    [GeneratedRegex(@"(Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+)")]
    private static partial Regex Summary();

    [GeneratedRegex(@"^\s+Failed\s+(?<name>[^\[\r\n]+?)\s*(\[[^\]]*\])?\s*$")]
    private static partial Regex FailedTest();

    [GeneratedRegex(@"Now listening on:\s*(?<url>https?://\S+)")]
    private static partial Regex Listening();
}
