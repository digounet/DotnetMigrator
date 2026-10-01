namespace Migrator.Core.Models;

public sealed record PackageInfo(string Id, string Version, bool DevelopmentDependency = false);

public sealed record PackageReferenceOut(string Id, string Version, bool PrivateAssetsAll = false);
