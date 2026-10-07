using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Migrator.Core.Analysis;

public enum AssemblyFlavor { Modern, NetFramework, Unknown, NotManaged }

/// <summary>How an API used by a binary behaves on .NET 10 / Linux.</summary>
public enum CompatibilityKind
{
    /// <summary>Not present in .NET 10 at all: the DLL throws when the member is first touched.</summary>
    Removed,
    /// <summary>Exists (shared framework or package) but throws PlatformNotSupportedException outside Windows.</summary>
    WindowsOnly,
    /// <summary>Exists, but behaves differently or is disabled by default (BinaryFormatter, AppDomain.CreateDomain, Thread.Abort, distributed transactions).</summary>
    Risky,
    /// <summary>Exists only as a NuGet package the migrated project must reference.</summary>
    Package
}

/// <param name="Api">What the DLL uses (namespace, type or member, or the native DLL for P/Invoke).</param>
/// <param name="Via">Dependency chain when the usage sits in a DLL this one references (e.g. "Legacy.Core.dll → Legacy.Util.dll"); null when direct.</param>
/// <param name="Signal">Architecture signal the usage implies (feeds the hosting decision), when any.</param>
public sealed record CompatibilityFinding(CompatibilityKind Kind, string Api, string Label, string? Via, Signal? Signal)
{
    public override string ToString() => Via == null ? Api : $"{Api} (via {Via})";
}

public sealed record AssemblyInspection(AssemblyFlavor Flavor, string? TargetFramework, bool ReferencesSystemWeb, IReadOnlyList<CompatibilityFinding> Findings, IReadOnlyList<string> Dependencies)
{
    public static AssemblyInspection Empty(AssemblyFlavor flavor) => new(flavor, null, false, [], []);
    public bool HasFindings => Findings.Count > 0;
    public IEnumerable<CompatibilityFinding> Of(CompatibilityKind kind) => Findings.Where(f => f.Kind == kind);
}

/// <summary>
/// Reads the metadata of a referenced DLL (no code runs): target framework, and every type reference, member reference and
/// P/Invoke that is removed from .NET 10, Windows-only, risky or package-only. DLLs the binary references are followed when
/// they sit next to it (the usual lib/ folder), so a clean wrapper over a dirty dependency is still reported.
/// </summary>
public static class AssemblyInspector
{
    private const int MaxDepth = 8;

    private sealed record ApiRule(string Namespace, string? Type, CompatibilityKind Kind, string Label, Signal? Signal, bool Prefix = true);

    /// <summary>Namespace (+ optional type name) → classification. Evaluated against TypeReferences, in order; first match wins.</summary>
    private static readonly ApiRule[] TypeRules =
    [
        // Still present on .NET 10 (shipped as System.Web.HttpUtility / packages): say nothing.
        new("System.Web", "HttpUtility", CompatibilityKind.Package, "", null),
        new("System.Web.Services", null, CompatibilityKind.Removed, "ASMX (System.Web.Services)", Signal.Asmx),
        new("System.Web", null, CompatibilityKind.Removed, "System.Web (ASP.NET clássico)", null),
        new("System.Runtime.Remoting", null, CompatibilityKind.Removed, ".NET Remoting", Signal.Remoting),
        new("System.EnterpriseServices", null, CompatibilityKind.Removed, "COM+ (System.EnterpriseServices)", Signal.ComPlus),
        new("System.ServiceModel", "ServiceHost", CompatibilityKind.Removed, "WCF servidor (ServiceHost)", Signal.WcfHost),
        new("System.ServiceModel.Activation", null, CompatibilityKind.Removed, "WCF servidor (hospedagem)", Signal.WcfHost),
        new("System.ServiceModel.Web", null, CompatibilityKind.Removed, "WCF REST (System.ServiceModel.Web)", Signal.WcfHost),
        new("System.ServiceModel", null, CompatibilityKind.Package, "WCF cliente (pacotes System.ServiceModel.*)", Signal.WcfClient),
        new("System.Data.Linq", null, CompatibilityKind.Removed, "LINQ to SQL", null),
        new("System.Data.OracleClient", null, CompatibilityKind.Removed, "System.Data.OracleClient (use Oracle.ManagedDataAccess.Core)", Signal.Oracle),
        new("System.Workflow", null, CompatibilityKind.Removed, "Windows Workflow Foundation", null),
        new("System.AddIn", null, CompatibilityKind.Removed, "System.AddIn", null),
        new("System.Messaging", null, CompatibilityKind.WindowsOnly, "MSMQ (System.Messaging)", Signal.Msmq),
        new("Microsoft.Win32", "Registry", CompatibilityKind.WindowsOnly, "Registro do Windows", Signal.Registry),
        new("System.Drawing", null, CompatibilityKind.WindowsOnly, "System.Drawing (GDI+)", Signal.SystemDrawing),
        new("System.Diagnostics", "EventLog", CompatibilityKind.WindowsOnly, "Event Log", Signal.EventLog),
        new("System.Diagnostics", "PerformanceCounter", CompatibilityKind.WindowsOnly, "PerformanceCounter", Signal.PerformanceCounter),
        new("System.DirectoryServices", null, CompatibilityKind.WindowsOnly, "Active Directory (System.DirectoryServices)", Signal.ActiveDirectory),
        new("System.Management", null, CompatibilityKind.WindowsOnly, "WMI (System.Management)", Signal.Wmi),
        new("System.ServiceProcess", null, CompatibilityKind.WindowsOnly, "Windows Service (System.ServiceProcess)", Signal.WindowsServiceHost),
        new("System.Windows.Forms", null, CompatibilityKind.WindowsOnly, "Windows Forms", Signal.WinForms),
        new("System.Windows", null, CompatibilityKind.WindowsOnly, "WPF", Signal.Wpf),
        new("System.Printing", null, CompatibilityKind.WindowsOnly, "System.Printing", null),
        new("System.Speech", null, CompatibilityKind.WindowsOnly, "System.Speech", null),
        new("System.Security.Principal", "WindowsIdentity", CompatibilityKind.WindowsOnly, "WindowsIdentity / impersonação", Signal.WindowsAuth),
        new("System.Security.Principal", "WindowsImpersonationContext", CompatibilityKind.WindowsOnly, "WindowsIdentity / impersonação", Signal.WindowsAuth),
        new("System.Security.AccessControl", null, CompatibilityKind.WindowsOnly, "ACLs do Windows (System.Security.AccessControl)", null),
        new("Microsoft.Web.Administration", null, CompatibilityKind.WindowsOnly, "administração do IIS", Signal.IisAdministration),
        new("Microsoft.Office.Interop", null, CompatibilityKind.WindowsOnly, "Office Interop", Signal.OfficeInterop),
        new("Microsoft.Office", null, CompatibilityKind.WindowsOnly, "Office Interop", Signal.OfficeInterop),
        new("CrystalDecisions", null, CompatibilityKind.WindowsOnly, "Crystal Reports", Signal.CrystalReports),
        new("Microsoft.Reporting", null, CompatibilityKind.WindowsOnly, "ReportViewer/RDLC", Signal.ReportViewer),
        new("System.Runtime.Serialization.Formatters.Binary", null, CompatibilityKind.Risky, "BinaryFormatter (desligado por padrão no .NET 9+)", Signal.BinaryFormatter),
        new("System.Transactions", "TransactionScope", CompatibilityKind.Risky, "TransactionScope (sem MSDTC no Linux)", null),
        new("System.Configuration", null, CompatibilityKind.Package, "System.Configuration.ConfigurationManager (lê app.config, não appsettings.json)", null),
        new("System.Data.SqlClient", null, CompatibilityKind.Package, "System.Data.SqlClient (pacote; prefira Microsoft.Data.SqlClient)", Signal.SqlServer),
        new("System.Runtime.Caching", null, CompatibilityKind.Package, "System.Runtime.Caching (pacote)", Signal.LocalCache),
        new("System.Security.Cryptography.Xml", null, CompatibilityKind.Package, "System.Security.Cryptography.Xml (pacote)", null),
        new("System.CodeDom", null, CompatibilityKind.Package, "System.CodeDom (pacote)", null),
        new("System.ComponentModel.Composition", null, CompatibilityKind.Package, "MEF (System.ComponentModel.Composition, pacote)", null),
    ];

    /// <summary>Members that exist in .NET 10 but throw or misbehave at run time.</summary>
    private static readonly Dictionary<string, (CompatibilityKind Kind, string Label, Signal? Signal)> MemberRules = new(StringComparer.Ordinal)
    {
        ["System.AppDomain::CreateDomain"] = (CompatibilityKind.Removed, "AppDomain.CreateDomain (PlatformNotSupportedException)", null),
        ["System.AppDomain::Unload"] = (CompatibilityKind.Removed, "AppDomain.Unload (PlatformNotSupportedException)", null),
        ["System.Threading.Thread::Abort"] = (CompatibilityKind.Removed, "Thread.Abort (PlatformNotSupportedException)", null),
        ["System.Threading.Thread::Suspend"] = (CompatibilityKind.Removed, "Thread.Suspend/Resume (PlatformNotSupportedException)", null),
        ["System.Threading.Thread::Resume"] = (CompatibilityKind.Removed, "Thread.Suspend/Resume (PlatformNotSupportedException)", null),
        ["System.Type::GetTypeFromProgID"] = (CompatibilityKind.WindowsOnly, "COM (Type.GetTypeFromProgID)", Signal.Com),
        ["System.Type::GetTypeFromCLSID"] = (CompatibilityKind.WindowsOnly, "COM (Type.GetTypeFromCLSID)", Signal.Com),
        ["System.Runtime.InteropServices.Marshal::GetActiveObject"] = (CompatibilityKind.WindowsOnly, "COM (Marshal.GetActiveObject)", Signal.Com),
        ["System.Runtime.InteropServices.Marshal::BindToMoniker"] = (CompatibilityKind.WindowsOnly, "COM (Marshal.BindToMoniker)", Signal.Com),
        ["System.Text.Encoding::GetEncoding"] = (CompatibilityKind.Risky, "Encoding.GetEncoding (code pages exigem CodePagesEncodingProvider)", null),
        ["System.Diagnostics.Process::Start"] = (CompatibilityKind.Risky, "Process.Start (executável precisa existir na imagem Linux)", null),
    };

    /// <summary>Native libraries that only exist on Windows (P/Invoke targets, compared without extension).</summary>
    private static readonly HashSet<string> WindowsNativeLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32", "user32", "advapi32", "ole32", "oleaut32", "shell32", "gdi32", "gdiplus", "winspool", "winspool.drv", "wininet", "winhttp",
        "crypt32", "ntdll", "netapi32", "mpr", "ws2_32", "comdlg32", "comctl32", "secur32", "wtsapi32", "psapi", "version", "shlwapi", "urlmon", "msvcrt", "odbc32"
    };

    /// <summary>Assemblies that belong to the framework (not followed on disk).</summary>
    private static bool IsFrameworkAssembly(string name) =>
        name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) || name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Presentation", StringComparison.OrdinalIgnoreCase) || name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Accessibility", StringComparison.OrdinalIgnoreCase) || name.StartsWith("UIAutomation", StringComparison.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, AssemblyInspection> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cached per path + size + last write, so the same DLL referenced by several projects is read once.</summary>
    public static AssemblyInspection Inspect(string path)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return AssemblyInspection.Empty(AssemblyFlavor.Unknown); }
        return Cache.GetOrAdd(key, _ => InspectUncached(path));
    }

    private static AssemblyInspection InspectUncached(string path)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new List<string>();
        var findings = new List<CompatibilityFinding>();
        var root = InspectOne(path, null, 0, visited, dependencies, findings);
        if (root == null) return AssemblyInspection.Empty(AssemblyFlavor.Unknown);
        var (flavor, tfm, referencesSystemWeb) = root.Value;
        if (flavor == AssemblyFlavor.NotManaged) return AssemblyInspection.Empty(AssemblyFlavor.NotManaged);
        // One line per API: the first (shallowest) occurrence wins, so the direct usage is reported before the transitive one.
        var distinct = findings.GroupBy(f => f.Api, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(f => f.Kind).ThenBy(f => f.Via == null ? 0 : 1).ThenBy(f => f.Api, StringComparer.OrdinalIgnoreCase).ToList();
        referencesSystemWeb |= distinct.Any(f => f.Api.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase) && f.Kind == CompatibilityKind.Removed);
        return new AssemblyInspection(flavor, tfm, referencesSystemWeb, distinct, dependencies);
    }

    private static (AssemblyFlavor Flavor, string? Tfm, bool SystemWeb)? InspectOne(string path, string? via, int depth, HashSet<string> visited, List<string> dependencies, List<CompatibilityFinding> findings)
    {
        if (!visited.Add(Path.GetFullPath(path))) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return (AssemblyFlavor.NotManaged, null, false);

            var md = pe.GetMetadataReader();
            var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tfm = TargetFramework(md);
            var flavor = tfm switch
            {
                not null when tfm.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) => AssemblyFlavor.NetFramework,
                not null when tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase) || tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) => AssemblyFlavor.Modern,
                _ when references.Contains("netstandard") || references.Contains("System.Runtime") => AssemblyFlavor.Modern,
                _ when references.Contains("mscorlib") => AssemblyFlavor.NetFramework,
                _ => AssemblyFlavor.Unknown
            };
            var systemWeb = references.Contains("System.Web") || references.Any(r => r.StartsWith("System.Web.", StringComparison.OrdinalIgnoreCase));

            CollectTypeReferences(md, via, findings);
            CollectMemberReferences(md, via, findings);
            CollectPInvokes(md, via, findings);
            // An assembly reference without a matching type reference still tells something (e.g. System.Web referenced for an attribute).
            if (systemWeb && !findings.Any(f => f.Api.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase)))
                findings.Add(new CompatibilityFinding(CompatibilityKind.Removed, "System.Web", "System.Web (ASP.NET clássico)", via, null));

            if (depth < MaxDepth)
            {
                var folder = Path.GetDirectoryName(path) ?? "";
                var self = Path.GetFileName(path);
                foreach (var name in references.Where(r => !IsFrameworkAssembly(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(folder, name + ".dll");
                    if (!File.Exists(candidate)) continue;
                    var chain = via == null ? self : $"{via} → {self}";
                    if (!dependencies.Contains(name, StringComparer.OrdinalIgnoreCase)) dependencies.Add(name);
                    InspectOne(candidate, chain, depth + 1, visited, dependencies, findings);
                }
            }
            return (flavor, tfm, systemWeb);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return depth == 0 ? (AssemblyFlavor.Unknown, null, false) : null;
        }
    }

    private static string? TargetFramework(MetadataReader md)
    {
        if (!md.IsAssembly) return null;
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            if (md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "TargetFrameworkAttribute") continue;
            var blob = md.GetBlobReader(attribute.Value);
            blob.ReadUInt16();
            return blob.ReadSerializedString();
        }
        return null;
    }

    private static void CollectTypeReferences(MetadataReader md, string? via, List<CompatibilityFinding> findings)
    {
        foreach (var handle in md.TypeReferences)
        {
            var type = md.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference) continue;      // nested types: the enclosing type is evaluated
            var ns = md.GetString(type.Namespace);
            var name = md.GetString(type.Name);
            if (ns.Length == 0) continue;
            var rule = Classify(ns, name);
            if (rule == null || rule.Label.Length == 0) continue;
            findings.Add(new CompatibilityFinding(rule.Kind, rule.Type == null ? ns : $"{ns}.{name}", rule.Label, via, rule.Signal));
        }
    }

    private static ApiRule? Classify(string ns, string typeName)
    {
        foreach (var rule in TypeRules)
        {
            var nsMatches = rule.Prefix
                ? ns.Equals(rule.Namespace, StringComparison.Ordinal) || ns.StartsWith(rule.Namespace + ".", StringComparison.Ordinal)
                : ns.Equals(rule.Namespace, StringComparison.Ordinal);
            if (!nsMatches) continue;
            if (rule.Type != null && !typeName.StartsWith(rule.Type, StringComparison.Ordinal)) continue;
            return rule;
        }
        return null;
    }

    private static void CollectMemberReferences(MetadataReader md, string? via, List<CompatibilityFinding> findings)
    {
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            var key = $"{md.GetString(type.Namespace)}.{md.GetString(type.Name)}::{md.GetString(member.Name)}";
            if (!MemberRules.TryGetValue(key, out var rule)) continue;
            findings.Add(new CompatibilityFinding(rule.Kind, key.Replace("::", "."), rule.Label, via, rule.Signal));
        }
    }

    private static void CollectPInvokes(MetadataReader md, string? via, List<CompatibilityFinding> findings)
    {
        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
            var import = method.GetImport();
            if (import.Module.IsNil) continue;
            var library = md.GetString(md.GetModuleReference(import.Module).Name);
            var bare = Path.GetFileNameWithoutExtension(library);
            if (!WindowsNativeLibraries.Contains(library) && !WindowsNativeLibraries.Contains(bare)) continue;
            findings.Add(new CompatibilityFinding(CompatibilityKind.WindowsOnly, $"P/Invoke {library}", $"P/Invoke em {library}", via, Signal.PInvoke));
        }
    }
}
