namespace NetRelay.Models;

public sealed record DiagnosticEvent(
    DateTimeOffset Timestamp,
    string Level,
    string Component,
    string Operation,
    string Outcome,
    string? RequestId = null,
    int? HttpStatus = null,
    long? Bytes = null,
    string? ExceptionType = null,
    int? HResult = null,
    string? Detail = null);
