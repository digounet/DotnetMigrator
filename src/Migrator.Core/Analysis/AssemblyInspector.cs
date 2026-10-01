using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Migrator.Core.Analysis;

public enum AssemblyFlavor { Modern, NetFramework, Unknown, NotManaged }

public sealed record AssemblyInspection(AssemblyFlavor Flavor, string? TargetFramework, bool ReferencesSystemWeb);

public static class AssemblyInspector
{
    public static AssemblyInspection Inspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new AssemblyInspection(AssemblyFlavor.NotManaged, null, false);

            var md = pe.GetMetadataReader();
            var references = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string? tfm = null;
            if (md.IsAssembly)
            {
                foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var attribute = md.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                    var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
                    if (md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "TargetFrameworkAttribute") continue;
                    var blob = md.GetBlobReader(attribute.Value);
                    blob.ReadUInt16();
                    tfm = blob.ReadSerializedString();
                }
            }

            var flavor = tfm switch
            {
                not null when tfm.StartsWith(".NETFramework", StringComparison.OrdinalIgnoreCase) => AssemblyFlavor.NetFramework,
                not null when tfm.StartsWith(".NETStandard", StringComparison.OrdinalIgnoreCase) || tfm.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) => AssemblyFlavor.Modern,
                _ when references.Contains("netstandard") || references.Contains("System.Runtime") => AssemblyFlavor.Modern,
                _ when references.Contains("mscorlib") => AssemblyFlavor.NetFramework,
                _ => AssemblyFlavor.Unknown
            };
            return new AssemblyInspection(flavor, tfm, references.Contains("System.Web") || references.Any(r => r.StartsWith("System.Web.", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException)
        {
            return new AssemblyInspection(AssemblyFlavor.Unknown, null, false);
        }
    }
}
