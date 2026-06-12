using NetRelay.Models;

namespace NetRelay.Services;

public static class RuleDefaultPolicy
{
    public static int GetDebounceSeconds(AppConfiguration config)
    {
        return config.DebounceSeconds;
    }

    public static int GetCooldownSeconds(AppConfiguration config)
    {
        return checked(config.CooldownMinutes * 60);
    }
}
