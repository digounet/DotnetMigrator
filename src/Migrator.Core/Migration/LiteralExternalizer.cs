using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>One literal the externalizer looked at: rewritten into a configuration read, or kept with the reason.</summary>
public sealed record ExternalizedLiteral(SettingKind Kind, string Key, string Value, string File, int Line, bool Rewritten, string? SkipReason);

/// <summary>
/// Replaces fixed URLs, e-mail addresses and credentials in C# string literals by configuration reads
/// (<c>System.Configuration.ConfigurationManager.AppSettings["Urls:ErpUrl"]</c>). On the .NET 10 path the regular
/// pipeline then turns that into <c>configuration["AppSettings:Urls:ErpUrl"]</c>; on the .NET Framework path it stays as is
/// and the key is added to the config. <c>const</c> fields become <c>static readonly</c>. Literals in attributes, switch cases,
/// parameter defaults and interpolated/verbatim strings are reported, never rewritten: the compiler could not accept the change.
/// </summary>
public static partial class LiteralExternalizer
{
    public const string UrlsSection = "Urls";
    public const string EmailsSection = "Emails";
    public const string SecretsSection = "Credenciais";
    public const string PathsSection = "Caminhos";
    /// <summary>Identifier given to the host argument of <c>new WebProxy("host", port)</c>.</summary>
    internal const string ProxyHostIdentifier = "ProxyHost";

    private static readonly string[] IgnoredUrlHosts = ["localhost", "127.0.0.1", "0.0.0.0", "tempuri.org", "www.w3.org", "schemas.xmlsoap.org", "purl.org", "xmlns"];
    private static readonly string[] IgnoredUrlHostSuffixes = [".w3.org", ".openxmlformats.org", ".microsoft.com", ".xmlsoap.org", ".oasis-open.org", ".schemas.com"];
    private static readonly string[] NamespaceIdentifiers = ["namespace", "ns", "xmlns", "xsd", "schema", "soapaction"];

    public sealed class Session
    {
        /// <summary>Value → key, so the same literal in two files gets one setting.</summary>
        internal Dictionary<string, string> KeysByValue { get; } = new(StringComparer.Ordinal);
        internal HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ExternalizedLiteral> Literals { get; } = [];
    }

    /// <summary>How the rewritten code reads the value: ConfigurationManager (legacy pipeline / .NET Framework) or the generated MigratorSettings helper (projects already on .NET).</summary>
    public enum ReadStyle { ConfigurationManager, MigratorSettings }

    public static string Externalize(string text, string file, Session session) => Externalize(text, file, session, ReadStyle.ConfigurationManager);

    public static string Externalize(string text, string file, Session session, ReadStyle style)
    {
        var sb = new StringBuilder(text.Length + 256);
        var last = 0;
        foreach (var literal in Literals(text))
        {
            var kind = Classify(literal.Body, literal.Identifier, literal.Prefix);
            if (kind == null) continue;
            var line = text[..literal.Start].Count(c => c == '\n') + 1;
            var skip = SkipReason(text, literal, kind.Value);
            if (skip != null)
            {
                session.Literals.Add(new ExternalizedLiteral(kind.Value, KeyFor(kind.Value, literal.Body, literal.Identifier, session, reserve: false), literal.Body, file, line, false, skip));
                continue;
            }
            var key = KeyFor(kind.Value, literal.Body, literal.Identifier, session, reserve: true);
            session.Literals.Add(new ExternalizedLiteral(kind.Value, key, literal.Body, file, line, true, null));

            var segmentStart = literal.StatementStart;
            var segment = text[segmentStart..literal.Start];
            if (literal.IsConst) segment = ConstKeyword().Replace(segment, "static readonly ", 1);
            sb.Append(text, last, segmentStart - last).Append(segment);
            sb.Append(style == ReadStyle.MigratorSettings ? $"MigratorSettings.Get(\"{ConfigPath(key)}\")" : $"System.Configuration.ConfigurationManager.AppSettings[\"{key}\"]");
            last = literal.End;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    /// <summary>Configuration keys without the AppSettings root, as the code reads them (Urls:ErpUrl).</summary>
    public static string ConfigPath(string key) => "AppSettings:" + key;

    // ---------------------------------------------------------------- classification

    internal static SettingKind? Classify(string body, string? identifier, string prefix)
    {
        if (body.Length < 4 || body.Contains('{') && prefix.Contains('$')) return null;
        if (identifier != null && NamespaceIdentifiers.Any(n => identifier.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;
        if (AwsAccessKey().IsMatch(body)) return SettingKind.Secret;
        if (PasswordInConnectionString().IsMatch(body) && !body.Contains("<secret:", StringComparison.Ordinal)) return SettingKind.Secret;
        if (identifier != null && CredentialIdentifier().IsMatch(identifier) && !NotCredential().IsMatch(identifier) && body.Length >= 6 && !body.StartsWith('{') && !body.StartsWith('<') && !body.StartsWith('$') && !body.StartsWith('%'))
            return SettingKind.Secret;
        if (identifier == ProxyHostIdentifier && ProxyHost().IsMatch(body)) return SettingKind.Url;
        if (UncPath().IsMatch(body)) return SettingKind.Path;
        var url = Url().Match(body);
        if (url.Success)
        {
            var host = url.Groups["host"].Value.ToLowerInvariant();
            if (IgnoredUrlHosts.Contains(host) || IgnoredUrlHostSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) return null;
            return SettingKind.Url;
        }
        if (Email().IsMatch(body)) return SettingKind.Email;
        return null;
    }

    private static string KeyFor(SettingKind kind, string value, string? identifier, Session session, bool reserve)
    {
        if (session.KeysByValue.TryGetValue(value, out var existing)) return existing;
        var section = kind switch { SettingKind.Url => UrlsSection, SettingKind.Email => EmailsSection, SettingKind.Path => PathsSection, _ => SecretsSection };
        var name = identifier != null ? Pascal(identifier) : kind switch
        {
            SettingKind.Url => Pascal(Url().Match(value).Groups["host"].Value),
            SettingKind.Email => Pascal(value.Split('@')[0]),
            SettingKind.Path => Pascal(value.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Pasta"),
            _ => "Segredo"
        };
        if (name.Length == 0) name = "Valor";
        var key = $"{section}:{name}";
        if (!reserve) return key;
        var candidate = key;
        for (var i = 2; session.Keys.Contains(candidate); i++) candidate = $"{key}{i}";
        session.Keys.Add(candidate);
        session.KeysByValue[value] = candidate;
        return candidate;
    }

    private static string Pascal(string raw)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in raw)
        {
            if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- literal scanning

    private sealed record Literal(int Start, int End, string Body, string Prefix, string? Identifier, bool IsConst, int StatementStart);

    private static IEnumerable<Literal> Literals(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') { var e = text.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? text.Length : e + 2; continue; }
            if (c == '\'') { i++; while (i < text.Length && text[i] != '\'') { if (text[i] == '\\') i++; i++; } i++; continue; }

            var start = i;
            var prefix = "";
            while (i < text.Length && (text[i] == '@' || text[i] == '$')) { prefix += text[i]; i++; }
            if (i >= text.Length || text[i] != '"') { if (prefix.Length == 0) i++; continue; }
            if (i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var e = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                var rawBody = e < 0 ? text[(i + 3)..] : text[(i + 3)..e];
                i = e < 0 ? text.Length : e + 3;
                yield return Make(text, start, i, rawBody, prefix + "\"\"\"");
                continue;
            }
            var verbatim = prefix.Contains('@');
            var sb = new StringBuilder();
            i++;
            var closed = false;
            while (i < text.Length)
            {
                var ch = text[i];
                if (ch == '"')
                {
                    if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                    i++; closed = true; break;
                }
                if (!verbatim && ch == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                if (!verbatim && ch == '\n') break;
                sb.Append(ch);
                i++;
            }
            if (!closed) continue;
            yield return Make(text, start, i, sb.ToString(), prefix);
        }
    }

    private static Literal Make(string text, int start, int end, string body, string prefix)
    {
        var statementStart = Math.Max(text.LastIndexOfAny([';', '{', '}'], Math.Max(0, start - 1)) + 1, 0);
        var before = text[statementStart..start];
        var assignment = AssignedIdentifier().Match(before);
        var identifier = assignment.Success ? assignment.Groups["id"].Value : null;
        // { "tokenPass", "value" } / headers.Add("Cookie", "value") / ["key"] = "value": the key literal names the value.
        if (identifier == null)
        {
            var pair = KeyedPair().Match(before);
            if (pair.Success) identifier = pair.Groups["id"].Value;
            else if (WebProxyHost().IsMatch(before)) identifier = ProxyHostIdentifier;
        }
        var isConst = assignment.Success && ConstKeyword().IsMatch(before);
        return new Literal(start, end, body, prefix, identifier, isConst, statementStart);
    }

    private static string? SkipReason(string text, Literal literal, SettingKind kind)
    {
        if (literal.Prefix.Contains('$')) return "string interpolada";
        // A verbatim literal is a single expression like any other: only the raw ones, and verbatim ones that are not a network path (their value can differ from the source text), stay.
        if (literal.Prefix.Contains("\"\"\"", StringComparison.Ordinal) || literal.Prefix.Contains('@') && kind != SettingKind.Path) return "string verbatim/raw";
        var before = text[literal.StatementStart..literal.Start];
        var trimmed = before.TrimStart();
        if (trimmed.StartsWith('[') || AttributeContext().IsMatch(before)) return "literal em atributo";
        if (CaseLabel().IsMatch(before)) return "rótulo de switch/case";
        if (ParameterDefault().IsMatch(before)) return "valor padrão de parâmetro";
        if (literal.Identifier == null && before.TrimEnd().EndsWith("const", StringComparison.Ordinal)) return "const sem identificador";
        return null;
    }

    [GeneratedRegex(@"(?<id>[A-Za-z_]\w*)\s*(?:=|:)\s*(?:new\s+Uri\s*\(\s*|new\s+MailAddress\s*\(\s*)?$")]
    private static partial Regex AssignedIdentifier();

    [GeneratedRegex(@"(?:^\s*|\.Add\(\s*|\[\s*)""(?<id>[A-Za-z][\w\- ]*)""\s*(?:,|\]\s*=)\s*$")]
    private static partial Regex KeyedPair();

    [GeneratedRegex(@"new\s+(?:System\.Net\.)?WebProxy\s*\(\s*$")]
    private static partial Regex WebProxyHost();

    [GeneratedRegex(@"^[\w\-]+(?:\.[\w\-]+)+$")]
    private static partial Regex ProxyHost();

    [GeneratedRegex(@"^\\\\[\w.\-]+\\\S")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"\bconst\s+")]
    private static partial Regex ConstKeyword();

    [GeneratedRegex(@"\[\s*[A-Za-z_][\w.]*\s*\([^)\]]*$")]
    private static partial Regex AttributeContext();

    [GeneratedRegex(@"\bcase\s*$")]
    private static partial Regex CaseLabel();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|override|virtual|async|partial|extern|unsafe)\s+)*[\w<>\[\],.?]+\s+\w+\s*\([^;{}]*\b[\w<>\[\],.?]+\s+\w+\s*=\s*$")]
    private static partial Regex ParameterDefault();

    [GeneratedRegex(@"^https?://(?<host>[\w.\-]+)(?::\d+)?(?:/[^\s""]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+(?:\s*[;,]\s*[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+)*$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^AKIA[0-9A-Z]{16}$")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(?:Password|Pwd)\s*=\s*(?![;""{$%<])[^;]{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInConnectionString();

    [GeneratedRegex(@"ApiKey|Api_Key|SecretKey|ClientSecret|AccessKey|SecretAccessKey|Password|Senha|Token|PrivateKey|Credential|Cookie", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialIdentifier();

    [GeneratedRegex(@"TokenType|TokenName|TokenHeader|PasswordField|PasswordLabel|PasswordHash|PasswordSalt|PasswordRegex|PasswordPattern|PasswordMin|PasswordMax|TokenPrefix|AntiForgery|ValidateAntiForgery|CancellationToken", RegexOptions.IgnoreCase)]
    private static partial Regex NotCredential();
}
