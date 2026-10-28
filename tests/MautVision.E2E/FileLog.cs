using Microsoft.Extensions.Logging;

/// <summary>The Core log of the run in one file (information and above), the place to look when a step fails.</summary>
internal sealed class FileLog(string path) : ILoggerFactory
{
    private readonly object gate = new();

    internal string Path { get; } = path;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {level,-11} {category}: {message}{(exception is null ? "" : Environment.NewLine + exception)}{Environment.NewLine}";
        lock (gate) File.AppendAllText(Path, line);
    }

    private sealed class Logger(FileLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
