namespace NetRelay.Services;

public enum PreNotificationAction
{
    Delay,
    Cancel
}

public sealed record NotificationProtocolAction(
    Guid NotificationId,
    string Token,
    PreNotificationAction Action);

public static class NotificationProtocolActivation
{
    public static string BuildUri(Guid notificationId, string token, PreNotificationAction action)
    {
        var actionValue = action switch
        {
            PreNotificationAction.Delay => "delay",
            PreNotificationAction.Cancel => "cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        return $"netrelay://notification?action={actionValue}&notificationId={notificationId:D}&token={Uri.EscapeDataString(token)}";
    }

    public static bool TryParse(string rawUrl, out NotificationProtocolAction? activation)
    {
        activation = null;
        try
        {
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, "netrelay", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "notification", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    return false;
                }

                var key = Uri.UnescapeDataString(part[..separatorIndex]);
                var value = Uri.UnescapeDataString(part[(separatorIndex + 1)..]);
                if (!values.TryAdd(key, value))
                {
                    return false;
                }
            }

            if (!values.TryGetValue("notificationId", out var notificationIdText)
                || !Guid.TryParse(notificationIdText, out var notificationId)
                || !values.TryGetValue("token", out var token)
                || string.IsNullOrWhiteSpace(token)
                || !values.TryGetValue("action", out var actionText))
            {
                return false;
            }

            var action = actionText.ToLowerInvariant() switch
            {
                "delay" => PreNotificationAction.Delay,
                "cancel" => PreNotificationAction.Cancel,
                _ => (PreNotificationAction?)null
            };

            if (action is null)
            {
                return false;
            }

            activation = new NotificationProtocolAction(notificationId, token, action.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
