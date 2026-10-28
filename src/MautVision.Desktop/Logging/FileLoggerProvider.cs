using System.IO;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Logging;

/// <summary>
/// Writes one log file per run. Records go through a bounded queue to a background writer, so logging
/// never blocks the caller; a failing disk drops records instead of affecting the application.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    internal const string FilePrefix = "maut-vision-";
    internal const int MaxFiles = 20;
    internal const long MaxFileBytes = 10L * 1024 * 1024;
    internal const long MaxTotalBytes = 50L * 1024 * 1024;
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<string> queue = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });
    private readonly StreamWriter writer;
    private readonly Task pump;
    private int disposed;

    private FileLoggerProvider(string filePath, StreamWriter writer)
    {
        FilePath = filePath;
        this.writer = writer;
        pump = Task.Run(PumpAsync);
    }

    public string FilePath { get; }

    /// <summary>Creates a new file in the directory and removes old files beyond the retention limits.</summary>
    public static FileLoggerProvider Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTime.UtcNow;
        var path = Path.Combine(directory, $"{FilePrefix}{now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        // FileShare.Read: another instance cannot delete a file that is still being written.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        // The BOM lets Windows PowerShell 5.1 and older viewers read Russian text correctly.
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        RemoveOldFiles(directory, path, now);
        return new FileLoggerProvider(path, writer);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>Completes the queue and waits a bounded time for the remaining records to reach the file.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        try
        {
            if (pump.Wait(FlushTimeout)) writer.Dispose();
        }
        catch (Exception)
        {
            // Shutdown continues even if the last records cannot be written.
        }
    }

    private void Enqueue(string record) => queue.Writer.TryWrite(record);

    private async Task PumpAsync()
    {
        long written = 0;
        try
        {
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var record))
                {
                    if (written >= MaxFileBytes) continue;
                    written += Encoding.UTF8.GetByteCount(record);
                    if (written >= MaxFileBytes)
                    {
                        record = Format(DateTime.UtcNow, LogLevel.Warning, typeof(FileLoggerProvider).FullName!,
                            $"Log file size limit of {MaxFileBytes} bytes reached; further records of this run are dropped.", null);
                    }
                    await writer.WriteAsync(record).ConfigureAwait(false);
                }
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The file is unusable; keep draining so that the queue does not hold memory.
            while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out _))
                {
                }
            }
        }
    }

    private static void RemoveOldFiles(string directory, string current, DateTime now)
    {
        try
        {
            var files = new DirectoryInfo(directory).EnumerateFiles(FilePrefix + "*.log")
                .Where(file => !string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
            // The current run may grow up to MaxFileBytes and counts as one of MaxFiles.
            var budget = MaxTotalBytes - MaxFileBytes;
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                budget -= file.Length;
                if (index < MaxFiles - 1 && budget >= 0 && now - file.LastWriteTimeUtc <= MaxAge) continue;
                try
                {
                    file.Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A file of a running instance stays until a later start.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Retention is retried on the next start.
        }
    }

    private static string Format(DateTime utc, LogLevel level, string category, string message, Exception? exception)
    {
        var record = new StringBuilder(128 + message.Length)
            .Append(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelName(level))
            .Append(' ').Append(category)
            .Append(": ").Append(message)
            .AppendLine();
        if (exception is not null) record.Append(exception).AppendLine();
        return record.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => level.ToString().ToUpperInvariant(),
    };

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && Volatile.Read(ref owner.disposed) == 0;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                owner.Enqueue(Format(DateTime.UtcNow, logLevel, category, formatter(state, exception), exception));
            }
            catch (Exception)
            {
                // A record that cannot be formatted is dropped; logging never throws into the caller.
            }
        }
    }
}
