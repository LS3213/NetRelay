using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Contracts;
using NetRelay.Contracts.Security;

namespace NetRelay.Services;

public sealed class UpdateService
{
    private readonly ConfigurationService _configService;
    private static readonly string RootPublicKey = OperationalKeyCertificate.DefaultRootPublicKeyBase64;

    public UpdateService(ConfigurationService configService)
    {
        _configService = configService;
        InitializeDefaultsFromBootstrap();
    }

    private void InitializeDefaultsFromBootstrap()
    {
        var config = _configService.Current;
        // If config values are uninitialized (or default), try reading bootstrap-config
        if (config.ClientConfigurationVersion == 0 && 
            config.PrimaryApiBaseUrl == "https://netrelay.lansil.cn" && 
            config.GithubRepository == "lansi/NetRelay")
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("NetRelay.bootstrap-config.json");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var json = reader.ReadToEnd();
                    var bootstrap = JsonSerializer.Deserialize<BootstrapConfig>(json);
                    if (bootstrap != null)
                    {
                        config.PrimaryApiBaseUrl = bootstrap.PrimaryApiBaseUrl;
                        config.GithubRepository = bootstrap.GithubRepository;
                        config.UpdateChannel = bootstrap.UpdateChannel;
                        config.AllowGithubFallback = bootstrap.AllowGithubFallback;
                        config.ClientConfigurationVersion = 1;
                        _configService.Save();
                    }
                }
            }
            catch
            {
                // Ignored, fallback to hardcoded defaults in AppConfiguration
            }
        }
    }

    public async Task<UpdateManifest?> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var now = DateTimeOffset.UtcNow;

        // Try primary source first
        try
        {
            var manifest = await CheckPrimarySourceAsync(config.PrimaryApiBaseUrl, config.UpdateChannel, currentVersion, now, cancellationToken);
            if (manifest != null)
            {
                return manifest;
            }
        }
        catch (Exception ex)
        {
            // Logging or tracing can be done here, now we proceed to fallback
            System.Diagnostics.Debug.WriteLine($"Primary update source failed: {ex.Message}");
        }

        // Fallback to GitHub source if enabled
        if (config.AllowGithubFallback && !string.IsNullOrWhiteSpace(config.GithubRepository))
        {
            try
            {
                return await CheckGitHubSourceAsync(config.GithubRepository, currentVersion, now, cancellationToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GitHub update fallback failed: {ex.Message}");
            }
        }

        return null;
    }

    private async Task<UpdateManifest?> CheckPrimarySourceAsync(
        string baseUrl,
        string channel,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        
        var requestUrl = $"{baseUrl.TrimEnd('/')}/api/v1/updates/latest?channel={channel}&architecture=win-x64&currentVersion={Uri.EscapeDataString(currentVersion)}";
        var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Add(Protocol.VersionHeader, Protocol.CurrentVersion.ToString());
        request.Headers.Add(Protocol.ClientVersionHeader, currentVersion);
        request.Headers.Add(Protocol.RequestIdHeader, Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse<UpdateCheckResponse>>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, 
            cancellationToken);

        if (apiResponse?.Data == null)
        {
            return null;
        }

        return VerifyAndExtractManifest(apiResponse.Data, now);
    }

    private async Task<UpdateManifest?> CheckGitHubSourceAsync(
        string repo,
        string currentVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");

        var requestUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        var response = await client.GetAsync(requestUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var versionString = tagName.TrimStart('v');

        if (Version.TryParse(versionString, out var latestVer) && Version.TryParse(currentVersion, out var currentVer))
        {
            if (latestVer <= currentVer)
            {
                return null;
            }
        }
        else if (string.Compare(versionString, currentVersion, StringComparison.OrdinalIgnoreCase) <= 0)
        {
            return null;
        }

        // Find manifest.json in assets
        string? manifestUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    manifestUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(manifestUrl))
        {
            return null;
        }

        // Download manifest and verify
        var manifestJson = await client.GetStringAsync(manifestUrl, cancellationToken);
        var checkResponse = JsonSerializer.Deserialize<UpdateCheckResponse>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (checkResponse == null)
        {
            return null;
        }

        return VerifyAndExtractManifest(checkResponse, now);
    }

    private UpdateManifest? VerifyAndExtractManifest(UpdateCheckResponse checkResponse, DateTimeOffset now)
    {
        // 1. Verify operational certificate using offline Root public key
        using var rootKey = ECDsa.Create();
        rootKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(RootPublicKey), out _);

        if (!checkResponse.Certificate.Verify(now, rootKey, "update-manifest"))
        {
            throw new CryptographicException("在线操作证书验证失败（签名不符或已过期）。");
        }

        // 2. Verify signature envelope
        using var operationalKey = ECDsa.Create();
        operationalKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(checkResponse.Certificate.PublicKey), out _);

        if (!checkResponse.Envelope.Verify("update-manifest", checkResponse.Envelope.Nonce, now, operationalKey))
        {
            throw new CryptographicException("更新清单签名校验失败。");
        }

        // 3. Extract UpdateManifest payload
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(checkResponse.Envelope.PayloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return manifest;
    }

    public async Task<string> DownloadPackageAsync(
        UpdateManifest manifest,
        Action<double>? progressCallback,
        CancellationToken cancellationToken)
    {
        var config = _configService.Current;
        var filename = "win-x64.zip"; // standard package filename
        string downloadUrl;

        // Determine download URL (try primary first)
        bool useGithub = false;
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            var headUrl = $"{config.PrimaryApiBaseUrl.TrimEnd('/')}/api/v1/updates/{manifest.Version}/download/{filename}";
            var headRequest = new HttpRequestMessage(HttpMethod.Head, headUrl);
            var headResponse = await client.SendAsync(headRequest, cancellationToken);
            if (headResponse.IsSuccessStatusCode)
            {
                downloadUrl = headUrl;
            }
            else
            {
                useGithub = true;
            }
        }
        catch
        {
            useGithub = true;
        }

        if (useGithub)
        {
            // Retrieve download URL from GitHub release assets
            if (string.IsNullOrWhiteSpace(config.GithubRepository))
            {
                throw new InvalidOperationException("未配置备用 GitHub 仓库，无法完成下载。");
            }
            downloadUrl = await GetGitHubAssetUrlAsync(config.GithubRepository, manifest.Version, filename, cancellationToken);
        }
        else
        {
            downloadUrl = $"{config.PrimaryApiBaseUrl.TrimEnd('/')}/api/v1/updates/{manifest.Version}/download/{filename}";
        }

        // Execute download with progress reporting and SHA256 hashing
        var tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetRelay", "updates");
        Directory.CreateDirectory(tempDirectory);
        var tempFilePath = Path.Combine(tempDirectory, $"update-{manifest.Version}.zip");

        using var httpClient = new HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(5);
        if (useGithub)
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");
        }

        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? manifest.PackageSize;

        using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destStream = File.Create(tempFilePath);
        using var sha256 = SHA256.Create();

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;

        while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
        {
            await destStream.WriteAsync(buffer, 0, read, cancellationToken);
            sha256.TransformBlock(buffer, 0, read, null, 0);

            totalRead += read;
            if (totalBytes > 0)
            {
                progressCallback?.Invoke((double)totalRead / totalBytes);
            }
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var downloadedHash = Convert.ToHexString(sha256.Hash!).ToLower();

        if (!string.Equals(downloadedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
            throw new CryptographicException($"下载包哈希值不匹配。预期: {manifest.Sha256}，实际: {downloadedHash}");
        }

        return tempFilePath;
    }

    private async Task<string> GetGitHubAssetUrlAsync(string repo, string version, string filename, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Client");

        var requestUrl = $"https://api.github.com/repos/{repo}/releases/tags/v{version}";
        var response = await client.GetAsync(requestUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Try tag without v prefix
            requestUrl = $"https://api.github.com/repos/{repo}/releases/tags/{version}";
            response = await client.GetAsync(requestUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (string.Equals(name, filename, StringComparison.OrdinalIgnoreCase))
                {
                    return asset.GetProperty("browser_download_url").GetString() ?? throw new InvalidOperationException("Asset browser_download_url is null.");
                }
            }
        }

        throw new FileNotFoundException($"在 GitHub Releases 资源列表中未找到包文件: {filename}");
    }

    private sealed class BootstrapConfig
    {
        [JsonPropertyName("primaryApiBaseUrl")]
        public string PrimaryApiBaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("githubRepository")]
        public string GithubRepository { get; set; } = string.Empty;

        [JsonPropertyName("updateChannel")]
        public string UpdateChannel { get; set; } = string.Empty;

        [JsonPropertyName("allowGithubFallback")]
        public bool AllowGithubFallback { get; set; }
    }
}
