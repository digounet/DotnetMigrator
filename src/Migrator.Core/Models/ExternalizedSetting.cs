namespace Migrator.Core.Models;

public enum SettingKind { Url, Email, Secret }

public enum SettingSource { Code, Config }

/// <summary>
/// A value that used to be fixed (in a C# literal or in appSettings) and now is configuration: URLs and e-mails become
/// IaC parameters delivered to the application (environment variables / Parameter Store), secrets become Secrets Manager
/// references. <see cref="Key"/> is the IConfiguration path (<c>AppSettings:Urls:ErpUrl</c>), the same key the code reads.
/// </summary>
public sealed class ExternalizedSetting
{
    public required string Project { get; init; }
    public required SettingKind Kind { get; init; }
    public required string Key { get; init; }
    /// <summary>Base value (the literal / the main config). Never the value of a secret in the IaC: secrets only carry <see cref="SecretName"/> there.</summary>
    public required string Value { get; init; }
    public required SettingSource Source { get; init; }
    public string? Location { get; init; }
    /// <summary>Secrets Manager name for <see cref="SettingKind.Secret"/>.</summary>
    public string? SecretName { get; init; }
    /// <summary>Values per environment known from config transforms (Development, Staging, Production).</summary>
    public Dictionary<string, string> EnvironmentValues { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Environment-variable form of the key (<c>AppSettings__Urls__ErpUrl</c>), what ECS/Lambda inject.</summary>
    public string EnvironmentVariable => Key.Replace(":", "__");

    /// <summary>Parameter Store path form (<c>Urls/ErpUrl</c>, without the AppSettings root), what EC2 instances read.</summary>
    public string ParameterPath => (Key.StartsWith("AppSettings:", StringComparison.Ordinal) ? Key["AppSettings:".Length..] : Key).Replace(':', '/');

    public string ValueFor(string environment) => EnvironmentValues.TryGetValue(environment, out var v) ? v : Value;
}
