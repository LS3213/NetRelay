namespace NetRelay.Services;

public static class AdapterIdentity
{
    public static bool AreEqual(string? first, string? second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Guid.TryParse(first, out var firstGuid)
            && Guid.TryParse(second, out var secondGuid)
            && firstGuid == secondGuid;
    }
}
