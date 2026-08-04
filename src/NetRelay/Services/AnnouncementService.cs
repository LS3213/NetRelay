using NetRelay.Contracts;
using NetRelay.Dialogs;
using Omnexa.Core;

namespace NetRelay.Services;

public sealed class AnnouncementService
{
    private readonly ConfigurationService _configService;
    private readonly OmnexaIntegrationService _omnexa;
    private readonly HashSet<string> _displayedThisLaunch =
        new(StringComparer.OrdinalIgnoreCase);

    public AnnouncementService(ConfigurationService configService)
    {
        _configService = configService;
        _omnexa = new OmnexaIntegrationService(configService);
    }

    public async Task CheckAndDisplayAnnouncementsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var control = App.PolicyService?.CurrentControl ??
                          await _omnexa.SyncAsync(cancellationToken);
            foreach (var announcement in control.Announcements
                         .OrderByDescending(item =>
                             AnnouncementPresentationPolicy.SeverityRank(item.Severity))
                         .ThenByDescending(item => item.PublishedAt))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!AnnouncementPresentationPolicy.ShouldDisplay(
                        announcement,
                        _configService.Current,
                        Protocol.ProductVersion,
                        _displayedThisLaunch))
                {
                    continue;
                }

                var dto = ToLegacyView(announcement);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var dialog = new AnnouncementDialog(dto);
                    if (System.Windows.Application.Current.MainWindow is { IsVisible: true } owner)
                    {
                        dialog.Owner = owner;
                    }

                    dialog.ShowDialog();
                });

                if (AnnouncementPresentationPolicy.MarkDisplayed(
                        announcement,
                        _configService.Current,
                        Protocol.ProductVersion,
                        _displayedThisLaunch))
                {
                    _configService.Save();
                }

                if (AnnouncementPresentationPolicy.RequiresShutdown(
                        announcement.Severity))
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => System.Windows.Application.Current.Shutdown());
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await new DiagnosticLogService().ErrorAsync(
                "omnexa",
                "announcements",
                exception);
        }
    }

    private static AnnouncementDto ToLegacyView(AnnouncementView announcement) =>
        new()
        {
            Id = announcement.Id,
            Title = announcement.Title,
            Content = announcement.Content,
            Severity = announcement.Severity,
            DisplayTrigger = announcement.Display,
            PublishedAt = announcement.PublishedAt,
            ExpiresAt = announcement.ExpiresAt
        };
}
