using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SpectraRealtekStudio.Core.Models;

namespace SpectraRealtekStudio.Core.Services;

/// <summary>
/// Checks the official GitHub Releases endpoint for newer versions of Spectra Realtek Studio.
/// </summary>
public sealed class UpdateCheckService
{
    public const string RepoUrl = "https://github.com/Creator-NL/SpectraRealtekStudio";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/Creator-NL/SpectraRealtekStudio/releases/latest";

    private static readonly HttpClient SharedHttp = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SpectraRealtekStudio/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(Version currentVersion, CancellationToken ct = default)
    {
        string currentStr = FormatVersion(currentVersion);
        var result = new UpdateCheckResult
        {
            CurrentVersion = currentStr,
            ReleaseUrl = $"{RepoUrl}/releases"
        };

        try
        {
            using var resp = await SharedHttp.GetAsync(LatestReleaseApiUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Success = false;
                result.ErrorMessage = $"GitHub API returned HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                return result;
            }

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? string.Empty) : string.Empty;
            string releaseName = root.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? tagName) : tagName;
            string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? result.ReleaseUrl) : result.ReleaseUrl;
            string body = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? string.Empty) : string.Empty;

            result.LatestVersion = string.IsNullOrWhiteSpace(tagName) ? currentStr : tagName.Trim();
            result.ReleaseTitle = releaseName;
            result.ReleaseUrl = htmlUrl;
            result.ReleaseNotesSummary = body.Length > 420 ? body.Substring(0, 420).TrimEnd() + "..." : body.Trim();

            if (TryParseSemanticVersion(tagName, out Version? latestVer) && latestVer != null)
            {
                Version curNorm = Normalize3Part(currentVersion);
                Version latNorm = Normalize3Part(latestVer);
                result.UpdateAvailable = latNorm > curNorm;
            }

            result.Success = true;
            return result;
        }
        catch (TaskCanceledException)
        {
            result.Success = false;
            result.ErrorMessage = "Update check timed out. Please check your internet connection.";
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = $"Could not reach GitHub Releases: {ex.Message}";
            return result;
        }
    }

    public static bool TryParseSemanticVersion(string? tag, out Version? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        string cleaned = tag.Trim();
        if (cleaned.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring(1);

        int dashIdx = cleaned.IndexOf('-');
        if (dashIdx >= 0)
            cleaned = cleaned.Substring(0, dashIdx);

        return Version.TryParse(cleaned, out parsed);
    }

    private static Version Normalize3Part(Version v)
    {
        return new Version(
            Math.Max(0, v.Major),
            Math.Max(0, v.Minor),
            Math.Max(0, v.Build));
    }

    public static string FormatVersion(Version v)
    {
        var n = Normalize3Part(v);
        return $"v{n.Major}.{n.Minor}.{n.Build}";
    }
}
