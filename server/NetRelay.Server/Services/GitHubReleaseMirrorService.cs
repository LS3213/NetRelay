using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetRelay.Contracts;
using NetRelay.Server.Configuration;
using NetRelay.Server.Data;

namespace NetRelay.Server.Services;

public sealed class GitHubReleaseMirrorService(IOptions<ServerOptions> options, ILogger<GitHubReleaseMirrorService> logger)
{
    private const string MetadataRootPath = "updates/stable/win-x64";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public bool IsEnabled => options.Value.GithubSyncEnabled;

    public async Task SyncPublishedReleaseAsync(
        Release release,
        string packagePath,
        UpdateCheckResponse latestResponse,
        IReadOnlyList<UpdateHistoryItem> history,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        using var client = CreateClient();
        var tag = BuildReleaseTag(release.Version);
        var remoteRelease = await EnsureReleaseAsync(client, tag, release, cancellationToken);
        await EnsureAssetAsync(client, remoteRelease, packagePath, options.Value.GithubAssetName, release.PackageSize, cancellationToken);
        await UpsertJsonFileAsync(client, $"{MetadataRootPath}/latest.json", latestResponse, $"sync stable latest {release.Version}", cancellationToken);
        await UpsertJsonFileAsync(client, $"{MetadataRootPath}/history.json", history, $"sync stable history {release.Version}", cancellationToken);
    }

    public async Task SyncRevokedStateAsync(
        UpdateCheckResponse? latestResponse,
        IReadOnlyList<UpdateHistoryItem> history,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();

        using var client = CreateClient();
        if (latestResponse is null)
        {
            await DeleteFileIfExistsAsync(client, $"{MetadataRootPath}/latest.json", "clear stable latest", cancellationToken);
        }
        else
        {
            await UpsertJsonFileAsync(client, $"{MetadataRootPath}/latest.json", latestResponse, "sync stable latest after revoke", cancellationToken);
        }

        await UpsertJsonFileAsync(client, $"{MetadataRootPath}/history.json", history, "sync stable history after revoke", cancellationToken);
    }

    private void EnsureConfigured()
    {
        if (!options.Value.GithubSyncEnabled)
        {
            throw new InvalidOperationException("GitHub 自动同步未启用。");
        }

        if (string.IsNullOrWhiteSpace(options.Value.GithubToken))
        {
            throw new InvalidOperationException("GitHub 自动同步缺少访问令牌。");
        }
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NetRelay-Server");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.GithubToken);
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private async Task<GitHubReleaseRecord> EnsureReleaseAsync(
        HttpClient client,
        string tag,
        Release release,
        CancellationToken cancellationToken)
    {
        var existing = await GetReleaseByTagAsync(client, tag, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var payload = new
        {
            tag_name = tag,
            name = tag,
            body = release.Changelog,
            draft = false,
            prerelease = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("releases"))
        {
            Content = JsonContent(payload)
        };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "创建 GitHub Release 失败", cancellationToken);
        return await DeserializeReleaseAsync(response, cancellationToken);
    }

    private async Task<GitHubReleaseRecord?> GetReleaseByTagAsync(HttpClient client, string tag, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(BuildApiUrl($"releases/tags/{Uri.EscapeDataString(tag)}"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "查询 GitHub Release 失败", cancellationToken);
        return await DeserializeReleaseAsync(response, cancellationToken);
    }

    private async Task EnsureAssetAsync(
        HttpClient client,
        GitHubReleaseRecord release,
        string packagePath,
        string assetName,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var existing = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.Size == expectedSize)
            {
                return;
            }

            using var deleteResponse = await client.DeleteAsync(BuildApiUrl($"releases/assets/{existing.Id}"), cancellationToken);
            await EnsureSuccessAsync(deleteResponse, "删除旧的 GitHub Release 资产失败", cancellationToken);
        }

        await using var fileStream = File.OpenRead(packagePath);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{release.UploadUrl}?name={Uri.EscapeDataString(assetName)}")
        {
            Content = new StreamContent(fileStream)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "上传 GitHub Release 资产失败", cancellationToken);
    }

    private async Task UpsertJsonFileAsync(HttpClient client, string path, object payload, string message, CancellationToken cancellationToken)
    {
        var serialized = JsonSerializer.Serialize(payload, JsonOptions);
        var existing = await GetContentFileAsync(client, path, cancellationToken);
        var body = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(serialized)),
            ["branch"] = options.Value.GithubPagesBranch
        };
        if (existing is not null)
        {
            body["sha"] = existing.Sha;
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, BuildApiUrl($"contents/{path}"))
        {
            Content = JsonContent(body)
        };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"更新 GitHub Pages 文件失败: {path}", cancellationToken);
    }

    private async Task DeleteFileIfExistsAsync(HttpClient client, string path, string message, CancellationToken cancellationToken)
    {
        var existing = await GetContentFileAsync(client, path, cancellationToken);
        if (existing is null)
        {
            return;
        }

        var body = new Dictionary<string, object?>
        {
            ["message"] = message,
            ["sha"] = existing.Sha,
            ["branch"] = options.Value.GithubPagesBranch
        };
        using var request = new HttpRequestMessage(HttpMethod.Delete, BuildApiUrl($"contents/{path}"))
        {
            Content = JsonContent(body)
        };
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"删除 GitHub Pages 文件失败: {path}", cancellationToken);
    }

    private async Task<GitHubContentRecord?> GetContentFileAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"{BuildApiUrl($"contents/{path}")}?ref={Uri.EscapeDataString(options.Value.GithubPagesBranch)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, $"查询 GitHub Pages 文件失败: {path}", cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return new GitHubContentRecord(document.RootElement.GetProperty("sha").GetString() ?? throw new InvalidOperationException("GitHub content sha is missing."));
    }

    private async Task<GitHubReleaseRecord> DeserializeReleaseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var uploadUrl = root.GetProperty("upload_url").GetString() ?? throw new InvalidOperationException("GitHub release upload_url is missing.");
        var markerIndex = uploadUrl.IndexOf('{');
        if (markerIndex >= 0)
        {
            uploadUrl = uploadUrl[..markerIndex];
        }

        var assets = new List<GitHubReleaseAssetRecord>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                assets.Add(new GitHubReleaseAssetRecord(
                    asset.GetProperty("id").GetInt64(),
                    asset.GetProperty("name").GetString() ?? string.Empty,
                    asset.GetProperty("size").GetInt64()));
            }
        }

        return new GitHubReleaseRecord(
            root.GetProperty("id").GetInt64(),
            uploadUrl,
            assets);
    }

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

    private string BuildApiUrl(string relativePath) =>
        $"https://api.github.com/repos/{options.Value.GithubRepository}/{relativePath}";

    private string BuildReleaseTag(string version) =>
        $"{options.Value.GithubReleaseTagPrefix}{version}";

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogError("GitHub API call failed. Status={StatusCode} Body={Body}", (int)response.StatusCode, body);
        throw new InvalidOperationException($"{message}（HTTP {(int)response.StatusCode}）。");
    }

    private sealed record GitHubReleaseRecord(long Id, string UploadUrl, IReadOnlyList<GitHubReleaseAssetRecord> Assets);
    private sealed record GitHubReleaseAssetRecord(long Id, string Name, long Size);
    private sealed record GitHubContentRecord(string Sha);
}
