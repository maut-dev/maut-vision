using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

/// <summary>Collects Core log records so that the smoke can check what reaches the application log.</summary>
internal sealed class LogCapture : ILoggerFactory
{
    private readonly ConcurrentQueue<LogRecord> records = new();

    internal IReadOnlyCollection<LogRecord> Records => records.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, records);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    internal sealed record LogRecord(LogLevel Level, string Category, string Message, Exception? Exception);

    private sealed class Logger(string category, ConcurrentQueue<LogRecord> records) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => records.Enqueue(new LogRecord(logLevel, category, formatter(state, exception), exception));
    }
}
