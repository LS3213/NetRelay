using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace NetRelay.Services;

public sealed record AutoStartResult(bool Success, string? ErrorMessage = null);

public sealed class AutoStartService
{
    private const string TaskName = "NetRelay AutoStart";

    public bool IsEnabled()
    {
        try
        {
            var result = RunTaskScheduler("/Query", "/TN", TaskName, "/XML");
            var executablePath = Environment.ProcessPath;
            return result.ExitCode == 0
                && !string.IsNullOrWhiteSpace(executablePath)
                && AutoStartTaskPolicy.MatchesCurrentExecutable(result.StandardOutput, executablePath);
        }
        catch
        {
            return false;
        }
    }

    public AutoStartResult SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                {
                    return new AutoStartResult(false, "无法确定当前 NetRelay 可执行文件路径。");
                }

                var taskDefinitionPath = CreateTaskDefinition(executablePath);
                ProcessResult result;
                try
                {
                    result = RunTaskScheduler(
                        "/Create",
                        "/TN", TaskName,
                        "/XML", taskDefinitionPath,
                        "/F");
                }
                finally
                {
                    try
                    {
                        File.Delete(taskDefinitionPath);
                    }
                    catch
                    {
                        // A leftover temporary definition does not invalidate the created task.
                    }
                }

                return result.ExitCode == 0
                    ? new AutoStartResult(true)
                    : new AutoStartResult(false, GetFailureMessage(result));
            }

            var deleteResult = RunTaskScheduler("/Delete", "/TN", TaskName, "/F");
            return deleteResult.ExitCode == 0 || !TaskExists()
                ? new AutoStartResult(true)
                : new AutoStartResult(false, GetFailureMessage(deleteResult));
        }
        catch (Exception exception)
        {
            return new AutoStartResult(false, exception.Message);
        }
    }

    private static string CreateTaskDefinition(string executablePath)
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法确定当前 Windows 用户 SID。");
        var workingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty;
        XNamespace taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var document = new XDocument(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(taskNamespace + "Task",
                new XAttribute("version", "1.4"),
                new XElement(taskNamespace + "RegistrationInfo",
                    new XElement(taskNamespace + "Description", "登录 Windows 后在系统托盘启动 NetRelay。")),
                new XElement(taskNamespace + "Triggers",
                    new XElement(taskNamespace + "LogonTrigger",
                        new XElement(taskNamespace + "Enabled", true),
                        new XElement(taskNamespace + "UserId", userSid))),
                new XElement(taskNamespace + "Principals",
                    new XElement(taskNamespace + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(taskNamespace + "UserId", userSid),
                        new XElement(taskNamespace + "LogonType", "InteractiveToken"),
                        new XElement(taskNamespace + "RunLevel", "HighestAvailable"))),
                new XElement(taskNamespace + "Settings",
                    new XElement(taskNamespace + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(taskNamespace + "DisallowStartIfOnBatteries", false),
                    new XElement(taskNamespace + "StopIfGoingOnBatteries", false),
                    new XElement(taskNamespace + "StartWhenAvailable", true),
                    new XElement(taskNamespace + "AllowStartOnDemand", true),
                    new XElement(taskNamespace + "Enabled", true),
                    new XElement(taskNamespace + "ExecutionTimeLimit", "PT0S")),
                new XElement(taskNamespace + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(taskNamespace + "Exec",
                        new XElement(taskNamespace + "Command", executablePath),
                        new XElement(taskNamespace + "Arguments", "--startup"),
                        new XElement(taskNamespace + "WorkingDirectory", workingDirectory)))));

        var taskDefinitionPath = Path.Combine(Path.GetTempPath(), $"NetRelay-AutoStart-{Guid.NewGuid():N}.xml");
        File.WriteAllText(taskDefinitionPath, document.ToString(), Encoding.Unicode);
        return taskDefinitionPath;
    }

    private static ProcessResult RunTaskScheduler(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows 任务计划程序命令。");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static bool TaskExists()
    {
        return RunTaskScheduler("/Query", "/TN", TaskName).ExitCode == 0;
    }

    private static string GetFailureMessage(ProcessResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return string.IsNullOrWhiteSpace(message)
            ? $"Windows 任务计划程序返回错误码 {result.ExitCode}。"
            : message.Trim();
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
