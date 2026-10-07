using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Migrator.Core.Analysis;

namespace Migrator.Tests;

/// <summary>
/// Local DLL inspection: type references, member references and P/Invokes are classified (removed / Windows-only / risky / package)
/// and DLLs next to the inspected one are followed. The fixtures are compiled in memory with Roslyn against fake reference
/// assemblies, so no .NET Framework installation is needed: the metadata (namespace + type names) is what the inspector reads.
/// </summary>
public sealed class AssemblyInspectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "migrator-inspector-" + Guid.NewGuid().ToString("N"));

    public AssemblyInspectorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static readonly MetadataReference[] Core = AppDomain.CurrentDomain.GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && (a.GetName().Name is "System.Runtime" or "System.Private.CoreLib" or "netstandard" or "System.Threading.Thread" or "System.Runtime.InteropServices" or "System.Text.Encoding.Extensions" or "System.Runtime.Serialization.Formatters" or "System.Diagnostics.Process" or "System.ComponentModel.Primitives"))
        .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location)).ToArray();

    /// <summary>Fake "framework" assembly exposing the legacy namespaces/types by name only.</summary>
    private static MetadataReference FakeFramework() => Compile("FakeFramework", """
        namespace System.Web { public class HttpContext { public static HttpContext Current; } public class HttpUtility { public static string UrlEncode(string s) => s; } }
        namespace System.Web.UI { public class Page { } }
        namespace Microsoft.Win32 { public class Registry { public static RegistryKey LocalMachine; } public class RegistryKey { public RegistryKey OpenSubKey(string n) => this; public object GetValue(string n) => n; } }
        namespace System.Diagnostics { public class EventLog { public static void WriteEntry(string s, string m) { } } }
        namespace System.Messaging { public class MessageQueue { } }
        namespace System.Configuration { public class ConfigurationManager { public static string Get(string k) => k; } }
        namespace System.Runtime.Remoting { public class RemotingConfiguration { public static void Configure(string f) { } } }
        """, Core, out _);

    private static MetadataReference Compile(string name, string source, IEnumerable<MetadataReference> references, out string path, string? dir = null, string tfm = ".NETFramework,Version=v4.8")
    {
        var lines = source.Split('\n');
        var usings = lines.Where(l => l.TrimStart().StartsWith("using ")).ToList();
        var rest = lines.Where(l => !l.TrimStart().StartsWith("using "));
        var tree = CSharpSyntaxTree.ParseText(string.Join("\n", usings) + $"\n[assembly: System.Runtime.Versioning.TargetFramework(\"{tfm}\")]\n" + string.Join("\n", rest));
        var compilation = CSharpCompilation.Create(name, [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        path = Path.Combine(dir ?? Path.GetTempPath(), name + ".dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return MetadataReference.CreateFromFile(path);
    }

    [Fact]
    public void Classifies_removed_windows_only_risky_and_package_apis()
    {
        var fx = FakeFramework();
        Compile("Legacy.Impressao", """
            using System;
            using System.Runtime.InteropServices;
            public static class Impressora
            {
                public static string Padrao() => Microsoft.Win32.Registry.LocalMachine.OpenSubKey("x").GetValue("y") as string ?? System.Configuration.ConfigurationManager.Get("Impressora");
                public static void Falha(string m) => System.Diagnostics.EventLog.WriteEntry("App", m);
                public static byte[] Bytes(string s) => System.Text.Encoding.GetEncoding(1252).GetBytes(s);
                public static void Fila() { var q = new System.Messaging.MessageQueue(); }
                public static void Encode(string s) => System.Web.HttpUtility.UrlEncode(s);
                [DllImport("winspool.drv")] public static extern bool OpenPrinter(string n, out IntPtr h, IntPtr d);
                [DllImport("libcustom.so")] public static extern int Custom();
            }
            """, Core.Append(fx), out var path, _dir);

        var inspection = AssemblyInspector.Inspect(path);
        Assert.Equal(AssemblyFlavor.NetFramework, inspection.Flavor);
        Assert.Equal(".NETFramework,Version=v4.8", inspection.TargetFramework);
        Assert.False(inspection.ReferencesSystemWeb);                                               // HttpUtility still exists on .NET 10
        Assert.Empty(inspection.Of(CompatibilityKind.Removed));
        var windows = inspection.Of(CompatibilityKind.WindowsOnly).Select(f => f.Label).ToList();
        Assert.Contains("Registro do Windows", windows);
        Assert.Contains("Event Log", windows);
        Assert.Contains("MSMQ (System.Messaging)", windows);
        Assert.Contains("P/Invoke em winspool.drv", windows);
        Assert.DoesNotContain(windows, w => w.Contains("libcustom"));                              // not a Windows library
        Assert.Contains(inspection.Of(CompatibilityKind.Risky), f => f.Api == "System.Text.Encoding.GetEncoding");
        Assert.Contains(inspection.Of(CompatibilityKind.Package), f => f.Api.StartsWith("System.Configuration"));
        Assert.Equal(new[] { Signal.Msmq, Signal.EventLog, Signal.Registry, Signal.PInvoke }.OrderBy(s => s), inspection.Findings.Where(f => f.Signal != null).Select(f => f.Signal!.Value).Distinct().OrderBy(s => s));
        Assert.All(inspection.Findings, f => Assert.Null(f.Via));
    }

    [Fact]
    public void Follows_dependencies_in_the_same_folder_and_reports_the_chain()
    {
        var fx = FakeFramework();
        var util = Compile("Legacy.Util", """
            public static class Util
            {
                public static object Contexto() => System.Web.HttpContext.Current;
                public static void Remoting() => System.Runtime.Remoting.RemotingConfiguration.Configure("app.config");
                public static void Thread() => new System.Threading.Thread(() => { }).Abort();
            }
            """, Core.Append(fx), out _, _dir);
        Compile("Legacy.Core", """
            public static class Core { public static object Contexto() => Util.Contexto(); }
            """, Core.Append(util), out var corePath, _dir);
        Compile("Legacy.Facade", """
            public static class Facade { public static object Contexto() => Core.Contexto(); }
            """, Core.Append(MetadataReference.CreateFromFile(corePath)), out var facadePath, _dir);

        var inspection = AssemblyInspector.Inspect(facadePath);
        Assert.Equal(["Legacy.Core", "Legacy.Util"], inspection.Dependencies);
        Assert.True(inspection.ReferencesSystemWeb);
        var removed = inspection.Of(CompatibilityKind.Removed).ToList();
        Assert.Contains(removed, f => f.Api == "System.Web" && f.Via == "Legacy.Facade.dll → Legacy.Core.dll");
        Assert.Contains(removed, f => f.Label == ".NET Remoting" && f.Via == "Legacy.Facade.dll → Legacy.Core.dll");
        Assert.Contains(removed, f => f.Api == "System.Threading.Thread.Abort");
        Assert.Contains(inspection.Findings, f => f.Signal == Signal.Remoting);

        // A clean modern DLL stays clean, and a missing file is reported as unknown instead of throwing.
        Compile("Clean", "public static class Clean { public static int X() => 1; }", Core, out var clean, _dir, ".NETStandard,Version=v2.0");
        var cleanInspection = AssemblyInspector.Inspect(clean);
        Assert.Equal(AssemblyFlavor.Modern, cleanInspection.Flavor);
        Assert.False(cleanInspection.HasFindings);
        Assert.Equal(AssemblyFlavor.Unknown, AssemblyInspector.Inspect(Path.Combine(_dir, "nao-existe.dll")).Flavor);
        File.WriteAllText(Path.Combine(_dir, "texto.dll"), "nao e um PE");
        Assert.Equal(AssemblyFlavor.Unknown, AssemblyInspector.Inspect(Path.Combine(_dir, "texto.dll")).Flavor);
    }
}
