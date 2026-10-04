using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace Migrator.Core.NuGet;

public enum LookupStatus { Found, NotFound, Unavailable }

public enum CompatStatus { Compatible, Incompatible, Unknown, NotFound, Unavailable }

public sealed record VersionList(LookupStatus Status, IReadOnlyList<NuGetVersion> Versions)
{
    public NuGetVersion? Latest(Func<NuGetVersion, bool>? filter = null) =>
        Versions.Where(v => !v.IsPrerelease && (filter?.Invoke(v) ?? true)).DefaultIfEmpty().Max();
}

public sealed record PackageCompatibility(CompatStatus Status, IReadOnlyList<string> Frameworks);

public sealed class NuGetClient : IDisposable
{
    private const string NuGetOrgFlatContainer = "https://api.nuget.org/v3-flatcontainer/";
    private const string NuGetOrgRegistration = "https://api.nuget.org/v3/registration5-gz-semver2/";

    private readonly HttpClient? _http;
    private readonly Lazy<Task<(string FlatContainer, string Registration)>> _resources;
    public NuGetSource Source { get; }
    private readonly SemaphoreSlim _throttle = new(8);
    private readonly ConcurrentDictionary<string, Task<VersionList>> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<PackageCompatibility>> _compat = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<(LookupStatus Status, JsonDocument? Leaf)>> _leaves = new(StringComparer.OrdinalIgnoreCase);
    private int _consecutiveFailures;

    private static readonly string[] DependencyGroupPriority =
    [
        "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netcoreapp3.0", "netcoreapp2.1",
        "netstandard2.1", "netstandard2.0", "netstandard1.6", "netstandard1.3", "netstandard1.0"
    ];

    public NuGetClient(bool offline, NuGetSource? source = null, HttpMessageHandler? handler = null)
    {
        Source = source ?? NuGetSource.NuGetOrg;
        // nuget.org endpoints are well known; any other feed (Artifactory, Nexus, Azure Artifacts) is discovered through its service index.
        // Offline: the lookups short-circuit before any request, so the constants are only placeholders.
        _resources = offline || Source.IsNuGetOrg
            ? new Lazy<Task<(string, string)>>(() => Task.FromResult((NuGetOrgFlatContainer, NuGetOrgRegistration)))
            : new Lazy<Task<(string, string)>>(ResolveResourcesAsync);
        if (offline) return;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NetFrameworkMigrator/1.0");
        if (Source.HasCredentials)
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{Source.UserName}:{Source.Password}")));
    }

    /// <summary>Reads the v3 service index and picks the PackageBaseAddress and RegistrationsBaseUrl resources.</summary>
    private async Task<(string FlatContainer, string Registration)> ResolveResourcesAsync()
    {
        var (status, index) = await GetJsonAsync(Source.IndexUrl);
        if (status != LookupStatus.Found || !index!.RootElement.TryGetProperty("resources", out var resources))
        {
            Volatile.Write(ref _consecutiveFailures, 5); // the feed is unusable: behave as offline instead of silently hitting nuget.org
            return (NuGetOrgFlatContainer, NuGetOrgRegistration);
        }
        string? flat = null; var registration = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources.EnumerateArray())
        {
            var type = r.TryGetProperty("@type", out var t) ? t.GetString() ?? "" : "";
            var id = r.TryGetProperty("@id", out var i) ? i.GetString() ?? "" : "";
            if (id.Length == 0) continue;
            if (type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.OrdinalIgnoreCase)) flat ??= id;
            if (type.StartsWith("RegistrationsBaseUrl", StringComparison.OrdinalIgnoreCase)) registration[type] = id;
        }
        var reg = registration.GetValueOrDefault("RegistrationsBaseUrl/3.6.0") ?? registration.GetValueOrDefault("RegistrationsBaseUrl/3.4.0")
                  ?? registration.GetValueOrDefault("RegistrationsBaseUrl/Versioned") ?? registration.GetValueOrDefault("RegistrationsBaseUrl") ?? registration.Values.FirstOrDefault();
        if (flat == null || reg == null) { Volatile.Write(ref _consecutiveFailures, 5); return (NuGetOrgFlatContainer, NuGetOrgRegistration); }
        return (flat.TrimEnd('/') + "/", reg.TrimEnd('/') + "/");
    }

    /// <summary>Quick reachability check of a feed's service index, used before the verification build so a blocked nuget.org fails in seconds instead of timing out restores.</summary>
    public static async Task<(bool Ok, string Detail)> ProbeAsync(NuGetSource source, TimeSpan timeout, HttpMessageHandler? handler = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = timeout };
            if (source.HasCredentials)
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{source.UserName}:{source.Password}")));
            using var response = await http.GetAsync(source.IndexUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode
                ? (true, $"{source.IndexUrl} respondeu {(int)response.StatusCode}")
                : (false, $"{source.IndexUrl} respondeu {(int)response.StatusCode} {response.ReasonPhrase}{(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? " (credenciais do nuget.config?)" : "")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return (false, $"{source.IndexUrl}: {(ex is TaskCanceledException ? $"sem resposta em {timeout.TotalSeconds:0}s" : ex.Message)}");
        }
    }

    public bool IsOnline => _http != null && Volatile.Read(ref _consecutiveFailures) < 5;

    public Task<VersionList> GetVersionsAsync(string id) =>
        _versions.GetOrAdd(id, key => FetchVersionsAsync(key));

    public Task<PackageCompatibility> GetCompatibilityAsync(string id, NuGetVersion version) =>
        _compat.GetOrAdd($"{id}/{version.ToNormalizedString()}", _ => FetchCompatibilityAsync(id, version));

    private async Task<VersionList> FetchVersionsAsync(string id)
    {
        var (flatContainer, _) = await _resources.Value;
        var (status, json) = await GetJsonAsync($"{flatContainer}{id.ToLowerInvariant()}/index.json");
        if (status != LookupStatus.Found) return new VersionList(status, []);

        var versions = new List<NuGetVersion>();
        foreach (var v in json!.RootElement.GetProperty("versions").EnumerateArray())
            if (NuGetVersion.TryParse(v.GetString(), out var parsed))
                versions.Add(parsed);
        return new VersionList(LookupStatus.Found, versions);
    }

    /// <summary>Minimum versions of the dependencies declared for the most modern framework group of the package.</summary>
    public async Task<IReadOnlyList<(string Id, NuGetVersion MinVersion)>> GetDependenciesAsync(string id, NuGetVersion version)
    {
        var (status, catalog) = await GetCatalogLeafAsync(id, version);
        if (status != LookupStatus.Found || !catalog!.RootElement.TryGetProperty("dependencyGroups", out var groups)) return [];

        var byFramework = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.EnumerateArray())
        {
            var tfm = group.TryGetProperty("targetFramework", out var tf) ? NormalizeTfm(tf.GetString() ?? "") : "";
            byFramework.TryAdd(tfm, group);
        }
        var chosen = DependencyGroupPriority.FirstOrDefault(byFramework.ContainsKey);
        if (chosen == null) return [];

        var result = new List<(string, NuGetVersion)>();
        if (byFramework[chosen].TryGetProperty("dependencies", out var deps))
            foreach (var dep in deps.EnumerateArray())
            {
                var depId = dep.TryGetProperty("id", out var i) ? i.GetString() : null;
                var range = dep.TryGetProperty("range", out var r) ? r.GetString() : null;
                if (depId != null && VersionRange.TryParse(range ?? "", out var parsed) && parsed.MinVersion != null)
                    result.Add((depId, parsed.MinVersion));
            }
        return result;
    }

    private static string NormalizeTfm(string tfm) => tfm.Trim().TrimStart('.').ToLowerInvariant();

    private Task<(LookupStatus Status, JsonDocument? Leaf)> GetCatalogLeafAsync(string id, NuGetVersion version) =>
        _leaves.GetOrAdd($"{id}/{version.ToNormalizedString()}", async _ =>
        {
            var (_, registration) = await _resources.Value;
            var leafUrl = $"{registration}{id.ToLowerInvariant()}/{version.ToNormalizedString().ToLowerInvariant()}.json";
            var (status, leaf) = await GetJsonAsync(leafUrl);
            if (status != LookupStatus.Found) return (status, null);
            if (!leaf!.RootElement.TryGetProperty("catalogEntry", out var catalogEntry) || catalogEntry.ValueKind != JsonValueKind.String)
                return (LookupStatus.Unavailable, null);
            return await GetJsonAsync(catalogEntry.GetString()!);
        });

    private async Task<PackageCompatibility> FetchCompatibilityAsync(string id, NuGetVersion version)
    {
        var (status, catalog) = await GetCatalogLeafAsync(id, version);
        if (status == LookupStatus.NotFound) return new PackageCompatibility(CompatStatus.NotFound, []);
        if (status == LookupStatus.Unavailable) return new PackageCompatibility(CompatStatus.Unavailable, []);

        var libFrameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasRootLib = false;
        if (catalog!.RootElement.TryGetProperty("packageEntries", out var entries))
        {
            foreach (var entry in entries.EnumerateArray())
            {
                var fullName = entry.TryGetProperty("fullName", out var fn) ? fn.GetString() ?? "" : "";
                var parts = fullName.Split('/');
                if (parts.Length < 2 || !(parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("ref", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (parts.Length == 2) hasRootLib = true;
                else libFrameworks.Add(parts[1]);
            }
        }

        if (libFrameworks.Count > 0)
            return new PackageCompatibility(
                libFrameworks.Any(TargetFrameworks.IsModern) ? CompatStatus.Compatible : CompatStatus.Incompatible,
                libFrameworks.Order().ToList());

        if (hasRootLib) return new PackageCompatibility(CompatStatus.Unknown, ["binário na raiz de lib/, sem framework declarado — típico de pacotes antigos para .NET Framework"]);

        var groups = new List<string>();
        if (catalog.RootElement.TryGetProperty("dependencyGroups", out var depGroups))
            foreach (var g in depGroups.EnumerateArray())
                if (g.TryGetProperty("targetFramework", out var tf) && tf.GetString() is { Length: > 0 } t)
                    groups.Add(t);

        if (groups.Count == 0) return new PackageCompatibility(CompatStatus.Unknown, []);
        return new PackageCompatibility(groups.Any(TargetFrameworks.IsModern) ? CompatStatus.Compatible : CompatStatus.Incompatible, groups);
    }

    private async Task<(LookupStatus Status, JsonDocument? Json)> GetJsonAsync(string url)
    {
        if (!IsOnline) return (LookupStatus.Unavailable, null);
        await _throttle.WaitAsync();
        try
        {
            using var response = await _http!.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                return (LookupStatus.NotFound, null);
            }
            response.EnsureSuccessStatusCode();
            var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            return (LookupStatus.Found, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Interlocked.Increment(ref _consecutiveFailures);
            return (LookupStatus.Unavailable, null);
        }
        finally
        {
            _throttle.Release();
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _throttle.Dispose();
    }
}

public static partial class TargetFrameworks
{
    public static bool IsModern(string tfm)
    {
        var t = tfm.Trim().TrimStart('.').ToLowerInvariant();
        if (t.StartsWith("netstandard") || t.StartsWith("netcoreapp")) return true;
        var m = ModernNet().Match(t);
        return m.Success && int.Parse(m.Groups[1].Value) >= 5;
    }

    public static bool IsNetFramework(string tfm)
    {
        var t = tfm.Trim().ToLowerInvariant();
        return t.StartsWith('v') || LegacyNet().IsMatch(t) || t.StartsWith(".netframework");
    }

    [GeneratedRegex(@"^net(\d+)\.\d+")]
    private static partial Regex ModernNet();

    [GeneratedRegex(@"^net[1-4]\d*$")]
    private static partial Regex LegacyNet();
}
