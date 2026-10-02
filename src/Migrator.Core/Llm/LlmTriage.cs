using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.Core.Llm;

/// <summary>
/// Uses the model to settle what regexes cannot: is a file write temporary or persistent? Is a static collection a cache
/// or application state? Is a timer job idempotent? The answers only adjust impact and add a note; they never delete items.
/// </summary>
public static partial class LlmTriage
{
    public const string System = """
        Você é um arquiteto .NET revisando trechos de código legado que vão rodar em containers na AWS. Para cada trecho, responda APENAS um JSON (sem texto fora dele) no formato:
        {"classificacao":"<valor>","confianca":<0..1>,"justificativa":"<uma frase em português>"}
        Valores possíveis de classificacao conforme a pergunta:
        - arquivos: "temporario" (arquivo de trabalho descartável, pode ficar em /tmp), "persistente" (precisa sobreviver ao processo/instância: uploads, exportações, integrações, logs de negócio) ou "misto".
        - estado_estatico: "cache" (dado reconstruível, aceitável divergir entre instâncias com expiração) ou "estado" (dado de negócio que precisa ser compartilhado/consistente entre instâncias).
        - job: "idempotente" (reexecutar não causa efeito duplicado) ou "nao_idempotente".
        Seja conservador: na dúvida, escolha a opção mais segura (persistente / estado / nao_idempotente) com confiança baixa.
        """;

    private static readonly Dictionary<string, (string Question, string LowImpactValue)> Rules = new(StringComparer.Ordinal)
    {
        ["MOD-ARCH-FILES"] = ("arquivos", "temporario"),
        ["MOD-CS-STATIC-STATE"] = ("estado_estatico", "cache"),
        ["MOD-CS-TIMERS"] = ("job", "idempotente")
    };

    public sealed record Triage(string Classification, double Confidence, string Justification);

    public static async Task<int> TriageAsync(LlmSession session, SolutionResult result, IProgress<string>? progress, CancellationToken cancellationToken, int maxItems = 12)
    {
        var count = 0;
        var candidates = result.Projects
            .SelectMany(p => p.Modernizations.Where(m => Rules.ContainsKey(m.RuleId) && m.Evidence != null).Select(m => (Project: p, Item: m)))
            .Take(maxItems).ToList();
        foreach (var (project, item) in candidates)
        {
            if (!session.Available) break;
            cancellationToken.ThrowIfCancellationRequested();
            var snippets = Snippets(project.Project.ProjectDir, item.Evidence!);
            if (snippets.Count == 0) continue;
            progress?.Report($"LLM: triagem de {item.RuleId} em {project.Project.Name}...");
            var (question, lowImpact) = Rules[item.RuleId];
            var answer = await session.TryCompleteAsync(System, UserMessage(question, item, snippets), cancellationToken);
            var triage = Parse(answer);
            if (triage == null) continue;
            count++;
            var note = $" Triagem por LLM ({session.Assistant.Name}): {triage.Classification} (confiança {triage.Confidence:0.0}) — {triage.Justification}";
            item.Why += note;
            if (triage.Classification.Equals(lowImpact, StringComparison.OrdinalIgnoreCase) && triage.Confidence >= 0.7 && item.Impact == Impact.High)
            {
                item.Impact = Impact.Medium;
                item.Proposal += item.RuleId switch
                {
                    "MOD-ARCH-FILES" => " Como a LLM classificou os arquivos como temporários, /tmp do container (storage efêmero) pode bastar; confirme antes de criar o bucket.",
                    "MOD-CS-STATIC-STATE" => " Como a LLM classificou como cache, IMemoryCache com expiração curta é aceitável; a divergência entre tasks não afeta o negócio.",
                    _ => " Como a LLM considerou o job idempotente, a tarefa agendada pode ter retry habilitado sem efeito duplicado."
                };
            }
        }
        return count;
    }

    /// <summary>Up to 3 locations "path:line" from the evidence, ±12 lines each, read from the original sources.</summary>
    internal static List<(string Location, string Code)> Snippets(string projectDir, string evidence)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Location().Matches(evidence))
        {
            if (list.Count == 3) break;
            var path = Path.Combine(projectDir, m.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var lines = File.ReadAllLines(path);
            var line = int.Parse(m.Groups["line"].Value);
            var from = Math.Max(0, line - 13);
            var to = Math.Min(lines.Length, line + 12);
            var sb = new StringBuilder();
            for (var i = from; i < to; i++) sb.Append(i + 1 == line ? ">> " : "   ").AppendLine(lines[i]);
            list.Add(($"{m.Groups["path"].Value}:{line}", sb.ToString()));
        }
        return list;
    }

    private static string UserMessage(string question, ModernizationItem item, List<(string Location, string Code)> snippets)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Pergunta: {question}");
        sb.AppendLine($"Item detectado por regra: [{item.RuleId}] {item.Title}");
        sb.AppendLine("Trechos (a linha marcada com >> é a ocorrência):");
        foreach (var (location, code) in snippets)
        {
            sb.AppendLine($"--- {location}");
            sb.AppendLine("```csharp");
            sb.Append(code);
            sb.AppendLine("```");
        }
        return sb.ToString();
    }

    internal static Triage? Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(answer[start..(end + 1)]);
            var root = doc.RootElement;
            var classification = root.TryGetProperty("classificacao", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(classification)) return null;
            var confidence = root.TryGetProperty("confianca", out var conf) && conf.ValueKind == JsonValueKind.Number ? conf.GetDouble() : 0.5;
            var justification = root.TryGetProperty("justificativa", out var j) ? j.GetString() ?? "" : "";
            return new Triage(classification.Trim().ToLowerInvariant(), Math.Clamp(confidence, 0, 1), justification.Trim());
        }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"(?<path>[^\s,()]+\.(?:cs|vb)):(?<line>\d+)")]
    private static partial Regex Location();
}
