using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces.Python;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Python;

internal enum WorkerOutcomeKind
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>
/// Outcome of a worker run. Failed carries a code without the area prefix (the consumer adds its own, e.g. training.worker_out_of_memory)
/// and a Russian message: the worker's own one for worker_&lt;code&gt;, otherwise the host's description with the last line of stderr.
/// </summary>
internal sealed record WorkerOutcome<T>(WorkerOutcomeKind Kind, T? Result, string? Code, string? Message)
{
    internal static WorkerOutcome<T> Success(T result) => new(WorkerOutcomeKind.Succeeded, result, null, null);

    internal static WorkerOutcome<T> Failure(string code, string message) => new(WorkerOutcomeKind.Failed, default, code, message);

    internal static WorkerOutcome<T> Cancelled() => new(WorkerOutcomeKind.Cancelled, default, null, null);
}

/// <summary>
/// The Python executor of Core (training now, model checks later). One process runs one operation over its job folder:
/// <c>request.json</c>, <c>input/</c> → <c>output/result.json</c>. The process is started from the launch description of the
/// host (never through a shell or PATH), with the job folder as its working directory and UTF-8 streams, inside a Job Object
/// that kills the tree when the host dies. stdout is the event protocol (line by line, sequence and size checked; also copied to
/// logs/events.jsonl), stderr goes to logs/stderr.log with its tail kept for the message. Cancellation ends the whole tree. The
/// outcome is decided by result.json, read only after the exit: exit code 0 without a valid result is a failure.
/// </summary>
internal sealed class PythonWorkers(PythonWorkerLaunch? launch, string catalogPath, ILogger logger) : IPythonRuntime
{
    internal const int ProtocolVersion = 1;
    private const int StderrTail = 4000;

    // Job objects of running workers; a smoke check closes them to imitate the death of the host.
    private readonly ConcurrentDictionary<WorkerJobObject, byte> running = new();

    internal PythonWorkerLaunch? Launch => launch;

    public PythonRuntimeStatus GetStatus()
    {
        var missing = CheckRuntime();
        return new PythonRuntimeStatus(launch is not null, missing is null, launch?.Mode, missing?.Code);
    }

    public IReadOnlyList<int> GetWorkerProcessIds() => running.Keys.SelectMany(job => job.ProcessIds()).Distinct().ToArray();

    /// <summary>runtime_not_configured or runtime_missing with a message; null when the described files exist.</summary>
    internal (string Code, string Message)? CheckRuntime()
    {
        if (launch is null) return ("runtime_not_configured", "Среда обучения не настроена: не задан Python worker.");
        if (!File.Exists(launch.Executable))
            return ("runtime_missing", $"Файл среды обучения не найден: {Path.GetFileName(launch.Executable)}.");
        if (launch.SourceDirectory is { } source && !File.Exists(Path.Combine(source, "worker", "__main__.py")))
            return ("runtime_missing", "Исходники Python worker не найдены.");
        return null;
    }

    /// <summary>
    /// Runs the operation of the prepared job folder and waits for the process tree to end. onEvent receives parsed events in
    /// order on one reader; parse reads the published result after the exit (its WorkerProtocolException is a failure).
    /// </summary>
    internal async Task<WorkerOutcome<T>> RunAsync<T>(string jobDirectory, string operationId, string operation, Func<WorkerEvent, Task> onEvent,
        Func<JsonElement, string, CancellationToken, Task<T>> parse, CancellationToken cancellationToken)
    {
        if (CheckRuntime() is { } missing) return WorkerOutcome<T>.Failure(missing.Code, missing.Message);
        if (cancellationToken.IsCancellationRequested) return WorkerOutcome<T>.Cancelled();
        var logs = Directory.CreateDirectory(Path.Combine(jobDirectory, "logs")).FullName;
        var output = Path.Combine(jobDirectory, "output");
        var watch = Stopwatch.StartNew();
        using var job = WorkerJobObject.Create();
        Process process;
        try
        {
            process = Start(jobDirectory);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogError(error, "Python worker {Executable} for operation {OperationId} did not start.", launch!.Executable, operationId);
            return WorkerOutcome<T>.Failure("worker_spawn_failed", $"Не удалось запустить Python worker: {error.Message}");
        }
        running[job] = 0;
        using var started = process;
        try
        {
            try
            {
                job.Assign(process);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                process.Kill(entireProcessTree: true);
                logger.LogError(error, "Python worker {Pid} could not join its job object.", process.Id);
                return WorkerOutcome<T>.Failure("worker_spawn_failed", "Не удалось поместить Python worker в объект задания Windows.");
            }
            process.StandardInput.Close();
            logger.LogInformation("Python worker {Pid} started for {Operation} {OperationId} ({Mode}).", process.Id, operation, operationId, launch!.Mode);
            WorkerProtocolException? violation = null;
            void Fail(WorkerProtocolException error)
            {
                if (Interlocked.CompareExchange(ref violation, error, null) is null) job.Terminate();
            }
            var stderr = DrainStderrAsync(process.StandardError.BaseStream, Path.Combine(logs, "stderr.log"));
            var stdout = ReadEventsAsync(process.StandardOutput.BaseStream, Path.Combine(logs, "events.jsonl"), operationId, onEvent, Fail);
            using (cancellationToken.Register(job.Terminate))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                // Children that outlived the parent (DataLoader workers) would keep the pipes open.
                job.Terminate();
                await stdout.ConfigureAwait(false);
            }
            var tail = await stderr.ConfigureAwait(false);
            var exitCode = process.ExitCode;
            logger.LogInformation("Python worker {Pid} for {OperationId} exited with code {ExitCode} after {Elapsed} ms.", process.Id, operationId, exitCode,
                watch.ElapsedMilliseconds);
            if (cancellationToken.IsCancellationRequested) return WorkerOutcome<T>.Cancelled();
            if (violation is { } protocol)
            {
                logger.LogWarning("Python worker {OperationId} broke the protocol: {Code} {Message}", operationId, protocol.Code, protocol.Message);
                return WorkerOutcome<T>.Failure(protocol.Code, protocol.Message);
            }
            var outcome = await ReadOutcomeAsync(output, operationId, operation, exitCode, tail, parse, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind == WorkerOutcomeKind.Failed)
            {
                logger.LogWarning("Python worker {OperationId} failed with {Code}; stderr tail: {Tail}", operationId, outcome.Code, LastLines(tail, 20));
            }
            return outcome;
        }
        finally
        {
            running.TryRemove(job, out _);
            job.Terminate();
        }
    }

    /// <summary>
    /// A result published by a worker that the host did not see finish (the application ended): accepted only when it is a valid
    /// success of this operation. Null when there is no result.json.
    /// </summary>
    internal async Task<WorkerOutcome<T>?> ReadPublishedAsync<T>(string jobDirectory, string operationId, string operation,
        Func<JsonElement, string, CancellationToken, Task<T>> parse, CancellationToken cancellationToken)
    {
        var output = Path.Combine(jobDirectory, "output");
        if (!File.Exists(Path.Combine(output, WorkerResults.ResultFileName))) return null;
        return await ReadOutcomeAsync(output, operationId, operation, 0, "", parse, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Smoke seam: closes the job objects of running workers, as the system does when the host process dies.</summary>
    internal void CloseAllForTesting()
    {
        foreach (var job in running.Keys) job.Dispose();
    }

    private async Task<WorkerOutcome<T>> ReadOutcomeAsync<T>(string output, string operationId, string operation, int exitCode, string stderrTail,
        Func<JsonElement, string, CancellationToken, Task<T>> parse, CancellationToken cancellationToken)
    {
        JsonDocument? document;
        try
        {
            document = await WorkerResults.ReadAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerProtocolException error)
        {
            return exitCode == 0 ? WorkerOutcome<T>.Failure(error.Code, error.Message) : ExitFailure<T>(exitCode, stderrTail);
        }
        using (document)
        {
            if (exitCode != 0)
            {
                return document is not null && WorkerResults.ReadFailure(document.RootElement, operationId, operation, ProtocolVersion) is { } failure
                    ? WorkerOutcome<T>.Failure("worker_" + failure.Code, failure.Message)
                    : ExitFailure<T>(exitCode, stderrTail);
            }
            if (document is null) return WorkerOutcome<T>.Failure("result_missing", "Работник завершился с кодом 0, но не записал output/result.json.");
            try
            {
                WorkerResults.CheckEnvelope(document.RootElement, operationId, operation, ProtocolVersion);
                if (WorkerResults.ReadFailure(document.RootElement, operationId, operation, ProtocolVersion) is { } failed)
                    return WorkerOutcome<T>.Failure("worker_" + failed.Code, failed.Message);
                return WorkerOutcome<T>.Success(await parse(document.RootElement, output, cancellationToken).ConfigureAwait(false));
            }
            catch (WorkerProtocolException error)
            {
                return WorkerOutcome<T>.Failure(error.Code, error.Message);
            }
            catch (IOException error)
            {
                return WorkerOutcome<T>.Failure("result_invalid", $"Файлы результата не читаются: {error.Message}");
            }
        }
    }

    private static WorkerOutcome<T> ExitFailure<T>(int exitCode, string stderrTail)
    {
        var last = LastLines(stderrTail, 1);
        var suffix = last.Length > 0 ? " Последняя строка журнала: " + (last.Length <= 1000 ? last : last[..1000]) : "";
        return exitCode switch
        {
            2 => WorkerOutcome<T>.Failure("worker_usage", "Работник не получил папку задачи." + suffix),
            3 => WorkerOutcome<T>.Failure("worker_result_not_written", "Работник не смог записать result.json." + suffix),
            4 => WorkerOutcome<T>.Failure("worker_already_ran", "В папке задачи уже есть результат: повтор запрещён." + suffix),
            130 => WorkerOutcome<T>.Failure("worker_interrupted", "Работник прерван извне." + suffix),
            _ => WorkerOutcome<T>.Failure("worker_crashed", $"Работник завершился с кодом {exitCode}." + suffix),
        };
    }

    private Process Start(string jobDirectory)
    {
        var start = new ProcessStartInfo(launch!.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = jobDirectory,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        // The working directory is the job folder; paths inside the request are relative to it.
        start.ArgumentList.Add(".");
        var environment = start.Environment;
        environment["PYTHONUTF8"] = "1";
        environment["PYTHONIOENCODING"] = "utf-8";
        environment["PYTHONUNBUFFERED"] = "1";
        environment["PYTHONDONTWRITEBYTECODE"] = "1";
        environment["MAUT_MODEL_CATALOG"] = catalogPath;
        environment.Remove("PYTHONHOME");
        if (launch.SourceDirectory is { } source) environment["PYTHONPATH"] = source;
        else environment.Remove("PYTHONPATH");
        return Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned no process.");
    }

    /// <summary>Reads JSON Lines from stdout; a line longer than any event may be stops the run as a protocol violation.</summary>
    private static async Task ReadEventsAsync(Stream stream, string eventsLog, string operationId, Func<WorkerEvent, Task> onEvent,
        Action<WorkerProtocolException> fail)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 16);
        var line = new ArrayBufferWriter<byte>(1 << 12);
        long sequence = 0;
        var broken = false;
        await using var log = new FileStream(eventsLog, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        async Task HandleAsync(ReadOnlyMemory<byte> bytes)
        {
            if (broken) return;
            if (bytes.Length > 0 && bytes.Span[^1] == (byte)'\r') bytes = bytes[..^1];
            if (bytes.Span.Trim((byte)' ').Length == 0) return;
            if (bytes.Length <= WorkerEventParser.RegularLineLimit)
            {
                await log.WriteAsync(bytes).ConfigureAwait(false);
                log.WriteByte((byte)'\n');
            }
            else
            {
                await log.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { omittedBytes = bytes.Length }) + "\n")).ConfigureAwait(false);
            }
            try
            {
                var (next, parsed) = WorkerEventParser.Parse(bytes, sequence, operationId, ProtocolVersion);
                sequence = next;
                if (parsed is not null) await onEvent(parsed).ConfigureAwait(false);
            }
            catch (WorkerProtocolException error)
            {
                broken = true;
                fail(error);
            }
        }
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                var chunk = buffer.AsMemory(0, read);
                while (chunk.Length > 0)
                {
                    var newline = chunk.Span.IndexOf((byte)'\n');
                    var part = newline < 0 ? chunk : chunk[..newline];
                    if (!broken)
                    {
                        if (line.WrittenCount + part.Length > WorkerEventParser.MaxLineLength)
                        {
                            broken = true;
                            fail(new WorkerProtocolException("protocol_event", $"Строка события работника превышает {WorkerEventParser.MaxLineLength / (1024 * 1024)} МиБ."));
                        }
                        else
                        {
                            line.Write(part.Span);
                        }
                    }
                    if (newline < 0) break;
                    await HandleAsync(line.WrittenMemory).ConfigureAwait(false);
                    line.ResetWrittenCount();
                    chunk = chunk[(newline + 1)..];
                }
            }
            if (line.WrittenCount > 0) await HandleAsync(line.WrittenMemory).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The pipe closed with the killed tree.
        }
        catch (Exception error)
        {
            // An observer failure stops the run: the host cannot follow it any more.
            broken = true;
            fail(new WorkerProtocolException("worker_io", $"Событие работника не обработано: {error.Message}"));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies stderr to the job log as it comes and returns its tail.</summary>
    private static async Task<string> DrainStderrAsync(Stream stream, string path)
    {
        var tail = new StringBuilder();
        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[1 << 14];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                // The job log is readable while the worker runs.
                await file.FlushAsync().ConfigureAwait(false);
                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                tail.Append(characters, 0, count);
                if (tail.Length > 2 * StderrTail) tail.Remove(0, tail.Length - StderrTail);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // The pipe closed with the killed tree, or the log could not be written; the tail is what was read.
        }
        return tail.Length <= StderrTail ? tail.ToString() : tail.ToString(tail.Length - StderrTail, StderrTail);
    }

    private static string LastLines(string text, int count)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(count));
    }
}
