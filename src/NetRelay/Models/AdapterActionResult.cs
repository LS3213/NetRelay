namespace NetRelay.Models;

public sealed record AdapterActionResult(
    bool Success,
    string Message,
    int? WindowsErrorCode = null);

