using System;
using System.IO;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace NetRelay.Services;

public static class RichToastService
{
    public static void Initialize()
    {
        RegisterAppIdAndProtocol();
    }

    private static void RegisterAppIdAndProtocol()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // 1. Register AppUserModelId for branding Windows Toast Notification
            using (var aumidKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\NetRelay.App"))
            {
                aumidKey.SetValue("DisplayName", "NetRelay");
                // Attempt to point to the Assets folder inside installation directory
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var iconPath = Path.Combine(baseDir, "Assets", "NetRelay.ico");
                if (File.Exists(iconPath))
                {
                    aumidKey.SetValue("IconUri", iconPath);
                }
            }

            // 2. Register netrelay:// custom URL Protocol for Toast interactive button callbacks
            using (var protocolKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\netrelay"))
            {
                protocolKey.SetValue("", "URL:NetRelay Protocol");
                protocolKey.SetValue("URL Protocol", "");

                using (var commandKey = protocolKey.CreateSubKey(@"shell\open\command"))
                {
                    commandKey.SetValue("", $"\"{exePath}\" --protocol-launch \"%1\"");
                }
            }
        }
        catch
        {
            // Fail gracefully (e.g., in virtualized or highly-restricted environments)
        }
    }

    public static void ShowPreNotification(PreNotificationEventArgs eventArgs, string actionName)
    {
        try
        {
            var xml = BuildPreNotificationXml(eventArgs, actionName);
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(xml);

            var notification = new ToastNotification(xmlDoc);
            ToastNotificationManager.CreateToastNotifier("NetRelay.App").Show(notification);
        }
        catch
        {
            // Suppress fallback exceptions
        }
    }

    private static string BuildPreNotificationXml(PreNotificationEventArgs eventArgs, string actionName)
    {
        var title = "NetRelay 自动化提醒";
        var content = $"规则 “{eventArgs.Rule.Name}” 将在 {eventArgs.MinutesRemaining} 分钟后执行：{actionName}网卡。";
        var actions = new List<string>();

        if (eventArgs.Notification.AllowDelay && eventArgs.Notification.DelayMinutes > 0)
        {
            var delayArgs = NotificationProtocolActivation.BuildUri(
                eventArgs.NotificationActionId,
                eventArgs.NotificationActionToken,
                PreNotificationAction.Delay);
            actions.Add($"""<action content="延迟 {eventArgs.Notification.DelayMinutes} 分钟" arguments="{SecurityElementEscape(delayArgs)}" activationType="protocol"/>""");
        }

        if (eventArgs.Notification.AllowCancelOccurrence)
        {
            var cancelArgs = NotificationProtocolActivation.BuildUri(
                eventArgs.NotificationActionId,
                eventArgs.NotificationActionToken,
                PreNotificationAction.Cancel);
            actions.Add($"""<action content="取消本次" arguments="{SecurityElementEscape(cancelArgs)}" activationType="protocol"/>""");
        }

        var actionsXml = actions.Count > 0
            ? $"<actions>{string.Join(string.Empty, actions)}</actions>"
            : string.Empty;

        return $@"
<toast scenario=""reminder"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{SecurityElementEscape(title)}</text>
      <text>{SecurityElementEscape(content)}</text>
    </binding>
  </visual>
  {actionsXml}
</toast>";
    }

    private static string SecurityElementEscape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
