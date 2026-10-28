using System.IO;
using System.Text.Json;
using MautVision.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Transport;

internal static class BridgeErrors
{
    internal const string DefaultMessage = "Не удалось выполнить действие. Повторите попытку.";

    internal static BridgeError From(Exception error, CancellationToken cancellationToken, IReadOnlyDictionary<string, string> messages)
    {
        return error switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => new("bridge.cancelled", "Запрос отменён."),
            CoreException core => new(core.Code, Format(MessageFor(core.Code, messages), core.Details), core.Details),
            JsonException => new("bridge.invalid_request", "Неверный формат сообщения."),
            ArgumentException => new("bridge.validation", "Проверьте параметры запроса."),
            FileNotFoundException => new("bridge.resource_unavailable", "Запрошенные данные не найдены."),
            IOException or UnauthorizedAccessException or InvalidDataException => new("bridge.storage_error", "Не удалось прочитать или записать данные. Проверьте доступ к каталогам приложения."),
            _ => new("bridge.internal_error", "Не удалось выполнить запрос.")
        };
    }

    /// <summary>Failures of storage and code are errors; a rejected request is a warning; a domain code is expected behaviour.</summary>
    internal static LogLevel LogLevelFor(string code) => code switch
    {
        "bridge.internal_error" or "bridge.storage_error" => LogLevel.Error,
        "bridge.cancelled" => LogLevel.Debug,
        _ when code.StartsWith("bridge.", StringComparison.Ordinal) => LogLevel.Warning,
        _ => LogLevel.Information,
    };

    internal static string MessageFor(string code, IReadOnlyDictionary<string, string> messages)
        => messages.TryGetValue(code, out var message) ? message : DefaultMessage;

    /// <summary>
    /// A text may name values of the error details in braces («Размер кадров датасета {datasetWidth} × {datasetHeight}…»); they are
    /// replaced when the details have them. Texts with placeholders are only for codes a request throws with those details.
    /// </summary>
    internal static string Format(string message, IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || !message.Contains('{', StringComparison.Ordinal)) return message;
        foreach (var (key, value) in details) message = message.Replace("{" + key + "}", value, StringComparison.Ordinal);
        return message;
    }
}
