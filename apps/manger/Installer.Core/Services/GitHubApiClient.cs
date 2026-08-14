using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Installer.Core.Models;
using Microsoft.Extensions.Logging;

namespace Installer.Core.Services;

/// <summary>
/// Exceptions specific to GitHub API operations
/// </summary>
public sealed class GitHubApiException : Exception
{
    /// <summary>
    /// HTTP status code from the response
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// Rate limit remaining requests
    /// </summary>
    public int? RateLimitRemaining { get; }

    public GitHubApiException(string message, HttpStatusCode? statusCode = null, int? rateLimitRemaining = null)
        : base(message)
    {
        StatusCode = statusCode;
        RateLimitRemaining = rateLimitRemaining;
    }
}

/// <summary>
/// GitHub release information (wire DTO aligned with GitHub REST API naming)
/// </summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTime PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> RawAssets { get; set; } = new();

    /// <summary>
    /// Assets mapped to installer model with platform/arch metadata inferred from file names
    /// </summary>
    [JsonIgnore]
    public List<ReleaseAsset> Assets => RawAssets.Select(AssetMapper.ToReleaseAsset).ToList();

    /// <summary>
    /// Extract version number from tag (e.g., "v1.0.0" -> "1.0.0")
    /// </summary>
    [JsonIgnore]
    public string Version => TagName.StartsWith("v", StringComparison.OrdinalIgnoreCase)
        ? TagName[1..]
        : TagName;
}

/// <summary>
/// Raw GitHub release asset DTO
/// </summary>
public sealed class GitHubAsset
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>
/// Maps GitHub wire assets to installer release assets (platform/arch inference)
/// </summary>
public static class AssetMapper
{
    public static ReleaseAsset ToReleaseAsset(GitHubAsset asset)
    {
        var (platform, arch, ext) = ParseFileName(asset.Name);
        return new ReleaseAsset
        {
            Url = asset.Url,
            BrowserDownloadUrl = asset.BrowserDownloadUrl,
            Name = asset.Name,
            Size = asset.Size,
            Platform = platform,
            Architecture = arch,
            Extension = ext
        };
    }

    /// <summary>
    /// Parse platform/architecture from asset file name like "app-1.0.0-win-x64.zip"
    /// </summary>
    public static (string Platform, string Architecture, string Extension) ParseFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            ext = ".tar.gz";

        var platform = "any";
        var arch = "any";

        if (fileName.Contains("win", StringComparison.OrdinalIgnoreCase)) platform = "win";
        else if (fileName.Contains("linux", StringComparison.OrdinalIgnoreCase)) platform = "linux";
        else if (fileName.Contains("osx", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Contains("macos", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Contains("mac-", StringComparison.OrdinalIgnoreCase)) platform = "macos";

        if (fileName.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("amd64", StringComparison.OrdinalIgnoreCase)) arch = "x64";
        else if (fileName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                 fileName.Contains("aarch64", StringComparison.OrdinalIgnoreCase)) arch = "arm64";
        else if (fileName.Contains("x86", StringComparison.OrdinalIgnoreCase) &&
                 !fileName.Contains("x86_64", StringComparison.OrdinalIgnoreCase)) arch = "x86";

        return (platform, arch, ext);
    }
}

/// <summary>
/// Result of a verified asset download
/// </summary>
public sealed record AssetDownloadResult(string FilePath, long Bytes, string Sha256);

/// <summary>
/// GitHub API client with retry logic and error handling
/// </summary>
public sealed class GitHubApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private const int MaxRetries = 5;

    public GitHubApiClient(HttpClient? httpClient = null, ILogger? logger = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
        _logger = logger;
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TinadecInstaller/0.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 10
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(300) };
    }

    /// <summary>
    /// Set GitHub token for authenticated requests (bypasses rate limits)
    /// </summary>
    public void SetAuthToken(string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Fetch all releases for a GitHub repository (owner/repo)
    /// </summary>
    public async Task<List<GitHubRelease>> GetReleasesAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30";

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new GitHubApiException($"Repository not found: {owner}/{repo}", HttpStatusCode.NotFound);
                }

                if ((int)response.StatusCode == 429 || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var remaining = ParseRateLimitHeader(response);
                    if (remaining == 0)
                    {
                        throw new GitHubApiException(
                            "GitHub API rate limit exceeded. Set a token via UseGitHubAuth to continue.",
                            response.StatusCode, remaining);
                    }
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new GitHubApiException(
                        $"GitHub API returned status code: {response.StatusCode}",
                        response.StatusCode,
                        ParseRateLimitHeader(response));
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
                    stream,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower },
                    cancellationToken) ?? new List<GitHubRelease>();

                _logger?.LogDebug("Fetched {Count} releases from {Owner}/{Repo}", releases.Count, owner, repo);
                return releases;
            }
            catch (GitHubApiException)
            {
                throw; // Non-retryable business errors (rate limit, not found)
            }
            catch (Exception ex) when (attempt < MaxRetries &&
                                       (ex is HttpRequestException || ex is TaskCanceledException))
            {
                _logger?.LogWarning(ex, "GitHub request failed, retrying (attempt {Attempt}/{Max})", attempt, MaxRetries);
                await Task.Delay(GetBackoffDelay(attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Download an asset to the specified path with progress reporting and SHA256 verification
    /// </summary>
    public async Task<AssetDownloadResult> DownloadAssetAsync(
        string url,
        string targetPath,
        Action<long, long>? progressCallback = null,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = targetPath + ".tmp_" + Guid.NewGuid().ToString("N")[..8];

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await DownloadCoreAsync(url, tempPath, progressCallback, cancellationToken);
                break;
            }
            catch (Exception ex) when (attempt < MaxRetries &&
                                       (ex is HttpRequestException || ex is TaskCanceledException))
            {
                _logger?.LogWarning(ex, "Download failed, retrying (attempt {Attempt}/{Max})", attempt, MaxRetries);
                TryDeleteFile(tempPath);
                await Task.Delay(GetBackoffDelay(attempt), cancellationToken);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }
        }

        var bytes = new FileInfo(tempPath).Length;
        var sha256 = await ComputeSha256Async(tempPath, cancellationToken);

        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(tempPath);
            throw new InvalidDataException(
                $"SHA256 mismatch for '{Path.GetFileName(targetPath)}': expected {expectedSha256}, got {sha256}");
        }

        File.Move(tempPath, targetPath, overwrite: true);
        _logger?.LogDebug("Downloaded {Path} ({Bytes} bytes)", targetPath, bytes);

        return new AssetDownloadResult(targetPath, bytes, sha256);
    }

    private async Task DownloadCoreAsync(
        string url,
        string tempPath,
        Action<long, long>? progressCallback,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Download failed with status code: {response.StatusCode}");
        }

        var total = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(tempPath);

        var buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            progressCallback?.Invoke(total, downloaded);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int? ParseRateLimitHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var remaining))
        {
            return remaining;
        }
        return null;
    }

    private static TimeSpan GetBackoffDelay(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt - 1), 30000));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
