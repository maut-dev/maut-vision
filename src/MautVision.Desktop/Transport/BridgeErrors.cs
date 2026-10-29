using System.IO;
using System.Text.Json;
using MautVision.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Transport;

/// <summary>
/// Turns an exception into the error of a reply: a code and safe details, never a text. The page translates the code (errors catalog of
/// the UI) and substitutes the details; the exception message goes only to the log.
/// </summary>
internal static class BridgeErrors
{
    internal static BridgeError From(Exception error, CancellationToken cancellationToken) => error switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => new("bridge.cancelled"),
        CoreException core => new(core.Code, core.Details),
        JsonException => new("bridge.invalid_request"),
        ArgumentException => new("bridge.validation"),
        FileNotFoundException => new("bridge.resource_unavailable"),
        IOException or UnauthorizedAccessException or InvalidDataException => new("bridge.storage_error"),
        _ => new("bridge.internal_error"),
    };

    /// <summary>Failures of storage and code are errors; a rejected request is a warning; a domain code is expected behaviour.</summary>
    internal static LogLevel LogLevelFor(string code) => code switch
    {
        "bridge.internal_error" or "bridge.storage_error" => LogLevel.Error,
        "bridge.cancelled" => LogLevel.Debug,
        _ when code.StartsWith("bridge.", StringComparison.Ordinal) => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}