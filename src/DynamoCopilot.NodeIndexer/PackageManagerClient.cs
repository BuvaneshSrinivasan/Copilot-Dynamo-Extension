using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.NodeIndexer;

public sealed record PackageInfo(string Name, DateTime LatestVersionUpdate, string DownloadUrl);

public sealed class PackageManagerClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private const string PackagesUrl  = "http://www.dynamopackages.com/packages";
    private const string DownloadBase = "http://www.dynamopackages.com/download/";

    /// <summary>
    /// Returns packages whose latest version was published after <paramref name="since"/>.
    /// Skips deprecated packages and any with no downloadable version.
    /// </summary>
    public async Task<List<PackageInfo>> GetPackagesUpdatedSinceAsync(
        DateTime since, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(PackagesUrl, ct);
        response.EnsureSuccessStatusCode();

        using var doc    = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var       result = new List<PackageInfo>();

        foreach (var pkg in doc.RootElement.GetProperty("content").EnumerateArray())
        {
            if (pkg.TryGetProperty("deprecated", out var dep) && dep.ValueKind == JsonValueKind.True)
                continue;

            if (!pkg.TryGetProperty("latest_version_update", out var updatedEl)) continue;
            if (!DateTime.TryParse(updatedEl.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var updated)) continue;
            // DateTime.MinValue means "get everything" (full rebuild mode)
            if (since != DateTime.MinValue && updated <= since) continue;

            if (!pkg.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!pkg.TryGetProperty("_id", out var idEl)) continue;
            var packageId = idEl.GetString();
            if (string.IsNullOrWhiteSpace(packageId)) continue;

            var downloadUrl = GetLatestVersionDownloadUrl(packageId, pkg);
            if (downloadUrl == null) continue;

            result.Add(new PackageInfo(name, updated, downloadUrl));
        }

        return result;
    }

    /// <summary>
    /// Downloads the package zip to <paramref name="destDir"/> and returns the local file path.
    /// </summary>
    public async Task<string> DownloadPackageAsync(
        PackageInfo pkg, string destDir, CancellationToken ct = default)
    {
        var safeName = string.Concat(pkg.Name.Select(
            c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var destPath = Path.Combine(destDir, safeName + ".zip");

        using var response = await _http.GetAsync(
            pkg.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fs = File.Create(destPath);
        await response.Content.CopyToAsync(fs, ct);
        return destPath;
    }

    private static string? GetLatestVersionDownloadUrl(string packageId, JsonElement pkg)
    {
        if (!pkg.TryGetProperty("versions", out var versions)) return null;

        // Pick the version with the most recent 'created' date
        JsonElement? latest     = null;
        var          latestDate = DateTime.MinValue;

        foreach (var ver in versions.EnumerateArray())
        {
            if (!ver.TryGetProperty("created", out var createdEl)) continue;
            if (!DateTime.TryParse(createdEl.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var created)) continue;
            if (created <= latestDate) continue;
            latestDate = created;
            latest     = ver;
        }

        if (latest == null) return null;
        if (!latest.Value.TryGetProperty("version", out var verEl)) return null;
        var versionStr = verEl.GetString();
        return string.IsNullOrWhiteSpace(versionStr) ? null : DownloadBase + packageId + "/" + versionStr;
    }

    public void Dispose() => _http.Dispose();
}
