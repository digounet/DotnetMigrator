using System.Security.Cryptography;
using System.Text;

namespace Migrator.Core.Llm;

/// <summary>
/// Caches answers on disk keyed by provider + prompts, so re-running the migrator (or the tests) gives the same
/// result without calling the model again. Wrap any <see cref="ILlmAssistant"/> with it.
/// </summary>
public sealed class CachedLlmAssistant(ILlmAssistant inner, string cacheDir) : ILlmAssistant
{
    public string Name => inner.Name;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public async Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(cacheDir, Safe(inner.Name), Hash(systemMessage + "\u0000" + userMessage) + ".txt");
        if (File.Exists(path))
        {
            Hits++;
            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        var answer = await inner.CompleteAsync(systemMessage, userMessage, cancellationToken);
        Misses++;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, answer, cancellationToken);
        }
        catch (IOException) { /* cache is best effort */ }
        catch (UnauthorizedAccessException) { }
        return answer;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Safe(string name) => new(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
}
