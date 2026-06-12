using NetRelay.Models;

namespace NetRelay.Services;

public static class ConnectivityProbePolicyValidator
{
    public static string? Validate(ConnectivityProbePolicy? policy)
    {
        if (policy is null)
        {
            return "联网探测策略不存在。";
        }

        if (policy.Endpoints is null || policy.Endpoints.Count == 0)
        {
            return "联网探测策略至少需要一个 HTTP/HTTPS 端点。";
        }

        if (policy.Endpoints.Any(endpoint =>
                !Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "ping" or "dns")))
        {
            return "联网探测端点必须是合法的 HTTP/HTTPS/PING/DNS 绝对地址。";
        }

        if (policy.TimeoutSeconds is < 0.5 or > 30)
        {
            return "联网探测超时时间必须在 0.5 到 30 秒之间。";
        }

        if (policy.Attempts is < 1 or > 10)
        {
            return "联网探测轮数必须在 1 到 10 之间。";
        }

        if (policy.RequiredFailedAttempts < 1 || policy.RequiredFailedAttempts > policy.Attempts)
        {
            return "断网失败轮数阈值必须在 1 到探测轮数之间。";
        }

        if (policy.RequiredSuccessfulEndpoints < 1
            || policy.RequiredSuccessfulEndpoints > policy.Endpoints.Count)
        {
            return "单轮成功端点数必须在 1 到端点总数之间。";
        }

        return null;
    }
}
