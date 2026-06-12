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

    public static string? ValidateConfig(AppConfiguration? config)
    {
        if (config is null)
        {
            return "配置不存在。";
        }

        var policyReason = Validate(config.ProbePolicy);
        if (policyReason is not null)
        {
            return policyReason;
        }

        if (config.DebounceSeconds is < 1 or > 60)
        {
            return "网络变化判定防抖时间必须在 1 到 60 秒之间。";
        }

        if (config.CooldownMinutes is < 0 or > 30)
        {
            return "规则执行冷却时间必须在 0 到 30 分钟之间。";
        }

        if (config.KeepDays is < 1 or > 90)
        {
            return "日志保留天数上限必须在 1 到 90 天之间。";
        }

        return null;
    }
}
