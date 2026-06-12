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

    public static void ShowPreNotification(string ruleName, string actionName, int delayMinutes, string ruleId)
    {
        try
        {
            var title = $"NetRelay 自动化提醒";
            var content = $"规则 “{ruleName}” 将在 {delayMinutes} 分钟后执行：{actionName}网卡。请确认是否延迟或取消。";

            // Prepare protocol query arguments
            var delayArgs = $"netrelay:action=delay&ruleId={ruleId}";
            var skipArgs = $"netrelay:action=skip&ruleId={ruleId}";

            var xml = $@"
<toast scenario=""reminder"">
  <visual>
    <binding template=""ToastGeneric"">
      <text>{SecurityElementEscape(title)}</text>
      <text>{SecurityElementEscape(content)}</text>
    </binding>
  </visual>
  <actions>
    <action content=""延迟 10 分钟"" arguments=""{SecurityElementEscape(delayArgs)}"" activationType=""protocol""/>
    <action content=""取消本次"" arguments=""{SecurityElementEscape(skipArgs)}"" activationType=""protocol""/>
  </actions>
</toast>";

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
