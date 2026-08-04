using NetRelay.Models;
using Omnexa.Core;

namespace NetRelay.Services;

public enum AnnouncementDisplayMode
{
    Once,
    OncePerVersion,
    EveryLaunch
}

public static class AnnouncementPresentationPolicy
{
    private const int MaximumHistoryEntries = 2048;

    public static bool ShouldDisplay(
        AnnouncementView announcement,
        AppConfiguration configuration,
        string clientVersion,
        ISet<string> displayedThisLaunch)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(displayedThisLaunch);

        var announcementId = announcement.Id.ToString("D");
        if (displayedThisLaunch.Contains(announcementId) ||
            !TryParseDisplayMode(announcement.Display, out var mode))
        {
            return false;
        }

        return mode switch
        {
            AnnouncementDisplayMode.Once =>
                !configuration.DisplayedAnnouncementIds.Contains(
                    announcementId,
                    StringComparer.OrdinalIgnoreCase),
            AnnouncementDisplayMode.OncePerVersion =>
                !configuration.DisplayedAnnouncementVersionKeys.Contains(
                    CreateVersionKey(announcement.Id, clientVersion),
                    StringComparer.OrdinalIgnoreCase),
            AnnouncementDisplayMode.EveryLaunch => true,
            _ => false
        };
    }

    public static bool MarkDisplayed(
        AnnouncementView announcement,
        AppConfiguration configuration,
        string clientVersion,
        ISet<string> displayedThisLaunch)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(displayedThisLaunch);

        var announcementId = announcement.Id.ToString("D");
        displayedThisLaunch.Add(announcementId);
        if (!TryParseDisplayMode(announcement.Display, out var mode))
        {
            return false;
        }

        return mode switch
        {
            AnnouncementDisplayMode.Once =>
                AddBounded(configuration.DisplayedAnnouncementIds, announcementId),
            AnnouncementDisplayMode.OncePerVersion =>
                AddBounded(
                    configuration.DisplayedAnnouncementVersionKeys,
                    CreateVersionKey(announcement.Id, clientVersion)),
            AnnouncementDisplayMode.EveryLaunch => false,
            _ => false
        };
    }

    public static bool TryParseDisplayMode(
        string? value,
        out AnnouncementDisplayMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "once":
            case "once_per_device":
                mode = AnnouncementDisplayMode.Once;
                return true;
            case "onceperversion":
            case "once_per_version":
                mode = AnnouncementDisplayMode.OncePerVersion;
                return true;
            case "everylaunch":
            case "every_startup":
                mode = AnnouncementDisplayMode.EveryLaunch;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static int SeverityRank(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "critical" => 2,
            "important" => 1,
            _ => 0
        };

    public static bool RequiresShutdown(string? severity) =>
        string.Equals(
            severity?.Trim(),
            "critical",
            StringComparison.OrdinalIgnoreCase);

    private static string CreateVersionKey(Guid announcementId, string clientVersion) =>
        $"{announcementId:D}|{clientVersion.Trim().ToLowerInvariant()}";

    private static bool AddBounded(List<string> values, string value)
    {
        if (values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        values.Add(value);
        if (values.Count > MaximumHistoryEntries)
        {
            values.RemoveRange(0, values.Count - MaximumHistoryEntries);
        }
        return true;
    }
}
