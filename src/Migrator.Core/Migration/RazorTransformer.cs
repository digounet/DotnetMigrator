using System.Text.RegularExpressions;

namespace Migrator.Core.Migration;

public static partial class BundleConfigParser
{
    private static readonly string[] AlwaysIgnored = [".intellisense.js", "-vsdoc.js", ".debug.js", ".map"];
    private static readonly string[] MinifiedSuffixes = [".min.js", ".min.css"];

    /// <param name="files">Project files relative to the project directory, using '/' separators.</param>
    public static Dictionary<string, List<string>> Parse(string bundleConfigSource, IReadOnlyCollection<string> files)
    {
        var bundles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match bundle in BundleDeclaration().Matches(bundleConfigSource))
        {
            var virtualPath = bundle.Groups["vp"].Value;
            var end = bundleConfigSource.IndexOf(';', bundle.Index);
            var segment = end < 0 ? bundleConfigSource[bundle.Index..] : bundleConfigSource[bundle.Index..end];

            var resolved = new List<string>();
            foreach (Match include in IncludeCall().Matches(segment))
                foreach (Match literal in StringLiteral().Matches(include.Groups["args"].Value))
                    resolved.AddRange(Resolve(literal.Groups[1].Value, files));

            foreach (Match dirInclude in IncludeDirectoryCall().Matches(segment))
            {
                var dir = dirInclude.Groups["dir"].Value.TrimStart('~').Trim('/');
                var recursive = dirInclude.Groups["sub"].Value == "true";
                var pattern = WildcardToRegex(dirInclude.Groups["pat"].Value);
                resolved.AddRange(files
                    .Where(f => f.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                    .Where(f => recursive || !f[(dir.Length + 1)..].Contains('/'))
                    .Where(f => pattern.IsMatch(Path.GetFileName(f)) && !IsIgnored(f))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(f => "~/" + f));
            }

            if (bundles.TryGetValue(virtualPath, out var existing)) existing.AddRange(resolved);
            else bundles[virtualPath] = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        return bundles;
    }

    private static IEnumerable<string> Resolve(string virtualPath, IReadOnlyCollection<string> files)
    {
        var relative = virtualPath.TrimStart('~').TrimStart('/');
        if (!relative.Contains('{') && !relative.Contains('*'))
        {
            var exact = files.FirstOrDefault(f => f.Equals(relative, StringComparison.OrdinalIgnoreCase));
            return exact != null ? ["~/" + exact] : [];
        }

        var dir = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        var pattern = WildcardToRegex(Path.GetFileName(relative));
        return files
            .Where(f => (dir.Length == 0 ? !f.Contains('/') : f.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase) && !f[(dir.Length + 1)..].Contains('/')))
            .Where(f => pattern.IsMatch(Path.GetFileName(f)) && !IsIgnored(f))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => "~/" + f);
    }

    private static bool IsIgnored(string file) =>
        AlwaysIgnored.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase)) ||
        MinifiedSuffixes.Any(s => file.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static Regex WildcardToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace(@"\{version}", @"\d+(?:\.\d+)+(?:-[\w]+)?")
            .Replace(@"\*", ".*");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"new\s+(?:System\.Web\.Optimization\.)?(?:ScriptBundle|StyleBundle|Bundle)\s*\(\s*""(?<vp>~[^""]*)""")]
    private static partial Regex BundleDeclaration();

    [GeneratedRegex(@"\.Include\s*\((?<args>[^)]*)\)")]
    private static partial Regex IncludeCall();

    [GeneratedRegex(@"\.IncludeDirectory\s*\(\s*""(?<dir>[^""]+)""\s*,\s*""(?<pat>[^""]+)""(?:\s*,\s*(?<sub>true|false))?")]
    private static partial Regex IncludeDirectoryCall();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex StringLiteral();
}

public static partial class RazorTransformer
{
    public static (string Text, List<CodeChange> Changes) Transform(string source, IReadOnlyDictionary<string, List<string>> bundles)
    {
        var changes = new Dictionary<(string, string), int>();
        void Record(string id, string description, int count)
        {
            if (count > 0) changes[(id, description)] = changes.GetValueOrDefault((id, description)) + count;
        }

        var eol = source.Contains("\r\n") ? "\r\n" : "\n";
        var text = RenderCall().Replace(source, m =>
        {
            var kind = m.Groups["kind"].Value;
            var indent = m.Groups["indent"].Value;
            var tags = new List<string>();
            foreach (Match arg in Literal().Matches(m.Groups["args"].Value))
            {
                var path = arg.Groups[1].Value;
                if (bundles.TryGetValue(path, out var files) && files.Count > 0)
                    tags.AddRange(files.Select(f => kind == "Scripts" ? ScriptTag(f) : StyleTag(f)));
                else if (!bundles.ContainsKey(path) && path.Contains('.'))
                    tags.Add(kind == "Scripts" ? ScriptTag(path) : StyleTag(path));
                else
                    return m.Value;
            }
            Record("VW-BUNDLE", "@Scripts.Render/@Styles.Render expandidos para <script>/<link> (bundles do BundleConfig)", 1);
            return indent + string.Join(eol + indent, tags);
        });

        text = Count(text, PartialCall(), "@await Html.PartialAsync(", c => Record("VW-PARTIAL", "@Html.Partial → @await Html.PartialAsync", c));
        text = Count(text, RenderPartialCall(), "await Html.RenderPartialAsync(", c => Record("VW-PARTIAL", "Html.RenderPartial → await Html.RenderPartialAsync", c));
        text = Count(text, IsAuthenticated(), "User.Identity.IsAuthenticated", c => Record("VW-REQUEST", "Request.IsAuthenticated → User.Identity.IsAuthenticated", c));
        text = Count(text, JsonEncode(), "System.Text.Json.JsonSerializer.Serialize(", c => Record("VW-JSON", "Json.Encode → System.Text.Json.JsonSerializer.Serialize", c));
        text = Count(text, SystemWebUsing(), "", c => Record("VW-USING", "@using System.Web.* removidos (namespaces equivalentes já são importados pelo Razor do ASP.NET Core)", c));
        text = Count(text, HttpContextCurrent(), "Context", c => Record("VW-CONTEXT", "HttpContext.Current → Context", c));
        text = Count(text, HandleErrorInfoModel(), "", c => Record("VW-ERRORMODEL", "@model HandleErrorInfo removido da view de erro (tipo inexistente no ASP.NET Core)", c));

        return (text, changes.Select(kv => new CodeChange(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList());
    }

    public static string BuildViewImports(IEnumerable<string> namespaces)
    {
        var usings = namespaces
            .Where(ns => !ns.StartsWith("System.Web", StringComparison.Ordinal) && ns != "System" && ns != "System.Linq" && ns != "System.Collections.Generic")
            .Distinct(StringComparer.Ordinal)
            .Select(ns => $"@using {ns}");
        return string.Join(Environment.NewLine, usings.Append("@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers")) + Environment.NewLine;
    }

    private static string ScriptTag(string path) => $"<script src=\"{path}\" asp-append-version=\"true\"></script>";
    private static string StyleTag(string path) => $"<link rel=\"stylesheet\" href=\"{path}\" asp-append-version=\"true\" />";

    private static string Count(string text, Regex regex, string replacement, Action<int> onCount)
    {
        var count = 0;
        var result = regex.Replace(text, _ => { count++; return replacement; });
        onCount(count);
        return result;
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)@(?:System\.Web\.Optimization\.)?(?<kind>Scripts|Styles)\.Render\((?<args>[^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex RenderCall();

    [GeneratedRegex(@"""([^""]+)""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"@Html\.Partial\(")]
    private static partial Regex PartialCall();

    [GeneratedRegex(@"(?<!await\s)\bHtml\.RenderPartial\(")]
    private static partial Regex RenderPartialCall();

    [GeneratedRegex(@"\bRequest\.IsAuthenticated\b")]
    private static partial Regex IsAuthenticated();

    [GeneratedRegex(@"\bJson\.Encode\(")]
    private static partial Regex JsonEncode();

    [GeneratedRegex(@"^[ \t]*@using\s+System\.Web(\.[\w.]+)?[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex SystemWebUsing();

    [GeneratedRegex(@"\bHttpContext\.Current\b")]
    private static partial Regex HttpContextCurrent();

    [GeneratedRegex(@"^[ \t]*@model\s+(System\.Web\.Mvc\.)?HandleErrorInfo[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex HandleErrorInfoModel();
}
