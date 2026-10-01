using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Migrator.Core.Migration;

public sealed record BuildDiagnostic(string Severity, string Code, string Message, string? File, int? Line, string? ProjectPath);

public sealed record BuildOutcome(bool Succeeded, bool TimedOut, int ExitCode, IReadOnlyList<BuildDiagnostic> Diagnostics, string Log);

public static partial class BuildVerifier
{
    public static async Task<BuildOutcome> BuildAsync(string target, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "build", target, "-nologo", "-v:q", "-tl:off", "-clp:NoSummary;ForceNoAlign", "-p:GenerateFullPaths=true", "-p:TreatWarningsAsErrors=false" })
            psi.ArgumentList.Add(arg);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

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
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        process.WaitForExit();

        var text = log.ToString();
        var diagnostics = Parse(text);
        return new BuildOutcome(!timedOut && process.ExitCode == 0, timedOut, timedOut ? -1 : process.ExitCode, diagnostics, text);
    }

    public static IReadOnlyList<BuildDiagnostic> Parse(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<BuildDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var m = DiagnosticLine().Match(line);
            if (!m.Success) continue;

            var file = m.Groups["file"].Value.Trim();
            int? lineNumber = int.TryParse(m.Groups["line"].Value, out var l) ? l : null;
            var project = m.Groups["proj"].Success ? m.Groups["proj"].Value.Trim() : null;
            if (project == null && file.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) project = file;
            if (file.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || !file.Contains(Path.DirectorySeparatorChar))
            {
                if (file.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) project = file;
                file = "";
            }

            var severity = m.Groups["sev"].Value.ToLowerInvariant() is "error" or "erro" ? "error" : "warning";
            var diagnostic = new BuildDiagnostic(severity, m.Groups["code"].Value, m.Groups["msg"].Value.Trim(),
                file.Length == 0 ? null : file, lineNumber, project);
            if (seen.Add($"{diagnostic.Code}|{diagnostic.File}|{diagnostic.Line}|{diagnostic.Message}|{diagnostic.ProjectPath}"))
                list.Add(diagnostic);
        }
        return list;
    }

    public static bool IsRelevantWarning(string code) =>
        code.StartsWith("NU", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("SYSLIB", StringComparison.OrdinalIgnoreCase) ||
        code is "CS0618" or "CS0612" or "CA1416" or "MSB3277" or "MSB3245" or "MSB3243";

    [GeneratedRegex(@"^(?<file>.+?)(?:\((?<line>\d+)(?:,\d+)*\))?\s*:\s*(?<sev>error|warning|erro|aviso|advertência)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex DiagnosticLine();
}
