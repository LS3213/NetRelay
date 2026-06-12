using System.IO;
using System.Xml.Linq;

namespace NetRelay.Services;

public static class AutoStartTaskPolicy
{
    public static bool MatchesCurrentExecutable(string taskXml, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(taskXml) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        try
        {
            var document = XDocument.Parse(taskXml);
            XNamespace taskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var command = document.Descendants(taskNamespace + "Command").SingleOrDefault()?.Value;
            var arguments = document.Descendants(taskNamespace + "Arguments").SingleOrDefault()?.Value;
            var enabled = document.Root?
                .Element(taskNamespace + "Settings")?
                .Element(taskNamespace + "Enabled")?
                .Value;

            return PathsEqual(command, executablePath)
                && string.Equals(arguments?.Trim(), "--startup", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(first.Trim().Trim('"')),
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
    }
}
