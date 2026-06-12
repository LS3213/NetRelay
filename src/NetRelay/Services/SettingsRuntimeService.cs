namespace NetRelay.Services;

public sealed record SettingsApplyResult(bool Success, string Message);

public sealed class SettingsRuntimeService
{
    private readonly ConfigurationService _configurationService;
    private readonly LogService _logService;
    private readonly Action _reloadScheduler;

    public SettingsRuntimeService(
        ConfigurationService configurationService,
        LogService logService,
        Action reloadScheduler)
    {
        _configurationService = configurationService;
        _logService = logService;
        _reloadScheduler = reloadScheduler;
    }

    public async Task<SettingsApplyResult> ApplyAsync()
    {
        var validationError = _configurationService.ValidateCurrent();
        if (validationError is not null)
        {
            _reloadScheduler();
            return new SettingsApplyResult(false, validationError);
        }

        try
        {
            _configurationService.Save();
            _reloadScheduler();
            await _logService.RotateLogsAsync(_configurationService.Current.KeepDays);
            return new SettingsApplyResult(true, "设置已保存并应用。");
        }
        catch (Exception exception)
        {
            return new SettingsApplyResult(false, exception.Message);
        }
    }
}
