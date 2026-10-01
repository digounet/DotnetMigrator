using Migrator.Core.Data;
using Migrator.Core.Migration;
using Migrator.Core.NuGet;

namespace Migrator.Tests;

public class InfrastructureTests
{
    [Theory]
    [InlineData("netstandard2.0", true)]
    [InlineData(".NETStandard1.3", true)]
    [InlineData("netcoreapp3.1", true)]
    [InlineData("net8.0", true)]
    [InlineData("net10.0-windows", true)]
    [InlineData("net472", false)]
    [InlineData(".NETFramework4.5", false)]
    [InlineData("netcore45", false)]
    [InlineData("portable-net45+win8", false)]
    public void Detects_modern_target_frameworks(string tfm, bool modern) =>
        Assert.Equal(modern, TargetFrameworks.IsModern(tfm));

    [Fact]
    public void Parses_msbuild_diagnostics_with_and_without_files()
    {
        const string output = """
            C:\out\Web\Controllers\HomeController.cs(12,5): error CS0246: The type or namespace name 'HttpPostedFileBase' could not be found [C:\out\Web\Web.csproj]
            C:\out\Web\Web.csproj : warning NU1701: Package 'iTextSharp 5.5.13' was restored using '.NETFramework,Version=v4.6.1' [C:\out\Shop.slnx]
            C:\out\Web\Controllers\HomeController.cs(12,5): error CS0246: The type or namespace name 'HttpPostedFileBase' could not be found [C:\out\Web\Web.csproj]
            Build FAILED.
            """;

        var diagnostics = BuildVerifier.Parse(output);

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(("error", "CS0246", 12), (diagnostics[0].Severity, diagnostics[0].Code, diagnostics[0].Line));
        Assert.Equal(@"C:\out\Web\Web.csproj", diagnostics[0].ProjectPath);
        Assert.Null(diagnostics[1].File);
        Assert.Equal(@"C:\out\Web\Web.csproj", diagnostics[1].ProjectPath);
    }

    [Fact]
    public void Build_hints_map_missing_system_web_types()
    {
        Assert.Contains("IFormFile", BuildHints.Suggest("CS0246", "The type or namespace name 'HttpPostedFile' could not be found"));
        Assert.Contains("IHttpContextAccessor", BuildHints.Suggest("CS0117", "'HttpContext' does not contain a definition for 'Current'"));
        Assert.Contains("BinaryFormatter", BuildHints.Suggest("SYSLIB0011", "'BinaryFormatter' is obsolete"));
    }

    [Theory]
    [InlineData("Microsoft.AspNet.Mvc", PackageAction.Remove)]
    [InlineData("Microsoft.AspNet.Mvc.pt-br", PackageAction.Remove)]
    [InlineData("jQuery", PackageAction.Remove)]
    [InlineData("System.ValueTuple", PackageAction.Remove)]
    [InlineData("Unity", PackageAction.Manual)]
    [InlineData("Microsoft.Owin.Security.Jwt", PackageAction.Replace)]
    [InlineData("Microsoft.Owin.Custom.Thing", PackageAction.Manual)]
    [InlineData("EntityFramework", PackageAction.Keep)]
    [InlineData("Microsoft.Extensions.Logging", PackageAction.Keep)]
    public void Package_rules_cover_classic_aspnet_stack(string id, PackageAction expected) =>
        Assert.Equal(expected, PackageRules.Find(id)!.Action);

    [Fact]
    public void Unknown_packages_have_no_rule() => Assert.Null(PackageRules.Find("Dapper"));

    [Fact]
    public void Project_file_writer_emits_sdk_project()
    {
        var spec = new ProjectFileSpec { Sdk = "Microsoft.NET.Sdk.Web" };
        spec.Properties.Add(("TargetFramework", "net10.0"));
        spec.Packages.Add(new Migrator.Core.Models.PackageReferenceOut("Newtonsoft.Json", "13.0.3"));
        spec.ProjectReferences.Add(@"..\Core\Core.csproj");
        spec.PostBuildCommand = "echo \"pronto\"";

        var xml = ProjectFileWriter.Write(spec);

        Assert.StartsWith("<Project Sdk=\"Microsoft.NET.Sdk.Web\">", xml);
        Assert.Contains("<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />", xml);
        Assert.Contains("<ProjectReference Include=\"..\\Core\\Core.csproj\" />", xml);
        Assert.Contains("AfterTargets=\"PostBuildEvent\"", xml);
        Assert.Contains("Command=\"echo &quot;pronto&quot;\"", xml);
    }
}
