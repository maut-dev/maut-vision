using MautVision.Desktop.Controllers.System.RequestDto;
using MautVision.Desktop.Controllers.System.ResponseDto;
using MautVision.Desktop.Logging;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Controllers.System;

/// <param name="ready">Called once, when the UI reports that the library is shown after startup.</param>
public sealed class SystemController(ILogger uiLogger, ILogger logger, Action ready)
{
    /// <summary>Category of records that come from the UI.</summary>
    public const string UiLogCategory = "MautVision.UI";
    public const int MaxLogMessageLength = 2000;
    public const int MaxLogStackLength = 8000;
    public const int MaxLogRecordsPerMinute = 20;

    private readonly object logLimitGate = new();
    private long logWindowStart = Environment.TickCount64;
    private int logWindowCount;
    private long readyAt = -1;

    public Task<PingResponseDto> PingAsync(PingRequestDto request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Message is null || request.Message.Length > 200)
        {
            throw new ArgumentException("Сообщение должно содержать не более 200 символов.");
        }
        return Task.FromResult(new PingResponseDto(request.Message, ".NET " + Environment.Version, DateTimeOffset.UtcNow));
    }

    /// <summary>Writes an unhandled UI error to the application log; records over the rate limit are dropped.</summary>
    public Task<LogResponseDto> LogAsync(LogRequestDto request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var level = request.Level switch
        {
            "error" => LogLevel.Error,
            "warning" => LogLevel.Warning,
            _ => throw new ArgumentException("Уровень записи должен быть error или warning."),
        };
        if (request.Message.Length is 0 or > MaxLogMessageLength)
        {
            throw new ArgumentException($"Сообщение должно содержать от 1 до {MaxLogMessageLength} символов.");
        }
        if (request.Stack is { Length: > MaxLogStackLength })
        {
            throw new ArgumentException($"Стек должен содержать не более {MaxLogStackLength} символов.");
        }
        if (!TryCountLogRecord(out var firstDropped))
        {
            if (firstDropped)
            {
                uiLogger.LogWarning("UI log limit of {Limit} records per minute reached; further records are dropped.", MaxLogRecordsPerMinute);
            }
            return Task.FromResult(new LogResponseDto(false));
        }
        if (request.Stack is null) uiLogger.Log(level, "{Message}", request.Message);
        else uiLogger.Log(level, "{Message}\n{Stack}", request.Message, request.Stack);
        return Task.FromResult(new LogResponseDto(true));
    }

    /// <summary>Records the startup stage library_ready once per run; later reports return the first time.</summary>
    public Task<ReadyResponseDto> ReadyAsync(ReadyRequestDto request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Projects < 0)
        {
            throw new ArgumentException("Число проектов не может быть отрицательным.");
        }
        var elapsed = StartupTimings.ElapsedMilliseconds;
        if (Interlocked.CompareExchange(ref readyAt, elapsed, -1) != -1)
        {
            return Task.FromResult(new ReadyResponseDto(Interlocked.Read(ref readyAt)));
        }
        StartupTimings.Log(logger, StartupTimings.LibraryReady, elapsed);
        logger.LogInformation("Library shown with {ProjectCount} projects.", request.Projects);
        ready();
        return Task.FromResult(new ReadyResponseDto(elapsed));
    }

    private bool TryCountLogRecord(out bool firstDropped)
    {
        lock (logLimitGate)
        {
            var now = Environment.TickCount64;
            if (now - logWindowStart >= 60_000)
            {
                logWindowStart = now;
                logWindowCount = 0;
            }
            logWindowCount++;
            firstDropped = logWindowCount == MaxLogRecordsPerMinute + 1;
            return logWindowCount <= MaxLogRecordsPerMinute;
        }
    }
}
