using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.BaseModels;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.BaseModels;

/// <summary>
/// Runtime-состояние одной модели каталога. Файл публикуется под своим именем только
/// после проверки размера и SHA-256; загрузка идёт во временный файл в той же папке.
/// </summary>
internal sealed class BaseModel : IBaseModel
{
    internal const string DownloadExtension = ".download";

    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    private readonly BaseModelCatalogEntry entry;
    private readonly string directory;
    private readonly string filePath;
    private readonly HttpClient http;
    private readonly Action<BaseModel> changed;
    private readonly ILogger logger;
    private readonly object gate = new();
    // Serialises reads, deletion and publication of the weights file.
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private BaseModelStatus status = BaseModelStatus.NotInstalled;
    private double progress;
    private string? errorCode;
    private Task? installation;
    private CancellationTokenSource? installationCancellation;
    private long lastProgressReport;

    internal BaseModel(BaseModelCatalogEntry entry, string directory, HttpClient http, Action<BaseModel> changed, ILogger logger)
    {
        this.entry = entry;
        this.directory = directory;
        this.http = http;
        this.changed = changed;
        this.logger = logger;
        filePath = Path.Combine(directory, entry.FileName);
    }

    public string Id => entry.Id;
    public string Family => entry.Family;
    public string Variant => entry.Variant;
    public long ByteSize => entry.ByteSize;
    public int Stride => entry.Stride;

    public BaseModelStatus Status
    {
        get { lock (gate) return status; }
    }

    public double Progress
    {
        get { lock (gate) return progress; }
    }

    public string? ErrorCode
    {
        get { lock (gate) return errorCode; }
    }

    /// <summary>Определяет состояние по размеру файла без чтения содержимого: полная проверка — VerifyIntegrityAsync.</summary>
    internal void InspectStoredFile()
    {
        var file = new FileInfo(filePath);
        lock (gate)
        {
            if (!file.Exists)
            {
                status = BaseModelStatus.NotInstalled;
                errorCode = null;
            }
            else if (file.Length == entry.ByteSize)
            {
                status = BaseModelStatus.Installed;
                errorCode = null;
            }
            else
            {
                status = BaseModelStatus.Failed;
                errorCode = "base_models.integrity_failed";
            }
        }
    }

    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        CancellationTokenSource cancellation;
        var started = false;
        lock (gate)
        {
            if (installation is null)
            {
                if (status == BaseModelStatus.Installed) return;
                cancellation = new CancellationTokenSource();
                status = BaseModelStatus.Installing;
                progress = 0;
                errorCode = null;
                installationCancellation = cancellation;
                // The lock is held until the field is assigned, so completion cannot clear it first.
                installation = Task.Run(() => RunInstallationAsync(cancellation.Token));
                started = true;
            }
            task = installation;
            cancellation = installationCancellation!;
        }
        if (started) changed(this);
        using (cancellationToken.Register(() => cancellation.Cancel()))
        {
            await task.ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task StopInstallationAsync()
    {
        Task? task;
        lock (gate)
        {
            task = installation;
            installationCancellation?.Cancel();
        }
        if (task is not null) await task.ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        RequireIdle();
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(filePath);
        }
        finally
        {
            fileGate.Release();
        }
        SetIdleState(BaseModelStatus.NotInstalled, null);
    }

    public async Task<bool> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
    {
        RequireIdle();
        bool valid;
        bool exists;
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            exists = File.Exists(filePath);
            valid = exists && await MatchesCatalogAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
        if (valid) SetIdleState(BaseModelStatus.Installed, null);
        else if (exists) SetIdleState(BaseModelStatus.Failed, "base_models.integrity_failed");
        else SetIdleState(BaseModelStatus.NotInstalled, null);
        return valid;
    }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("Destination stream must be writable.", nameof(destination));
        if (Status != BaseModelStatus.Installed) throw NotInstalled();
        await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream source;
            try
            {
                source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                SetIdleState(BaseModelStatus.NotInstalled, null);
                throw NotInstalled();
            }
            await using (source)
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            fileGate.Release();
        }
    }

    private async Task RunInstallationAsync(CancellationToken cancellationToken)
    {
        var result = BaseModelStatus.Failed;
        string? code = "base_models.install_failed";
        Exception? failure = null;
        try
        {
            await DownloadAsync(cancellationToken).ConfigureAwait(false);
            result = BaseModelStatus.Installed;
            code = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = BaseModelStatus.NotInstalled;
            code = null;
        }
        catch (OperationCanceledException error)
        {
            code = "base_models.download_failed";
            failure = error;
        }
        catch (CoreException error)
        {
            code = error.Code;
            failure = error;
        }
        catch (HttpRequestException error)
        {
            code = "base_models.download_failed";
            failure = error;
        }
        catch (IOException error) when (error.HResult is DiskFullHResult or HandleDiskFullHResult)
        {
            code = "base_models.disk_full";
            failure = error;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            code = "base_models.storage_failed";
            failure = error;
        }
        catch (Exception error)
        {
            // An unexpected failure still leaves the model in a reportable Failed state.
            failure = error;
        }
        if (failure is not null)
        {
            logger.LogError(failure, "Base model {ModelId} installation failed with {ErrorCode}.", entry.Id, code);
        }
        lock (gate)
        {
            status = result;
            progress = 0;
            errorCode = code;
            installation = null;
            installationCancellation = null;
        }
        changed(this);
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{entry.FileName}.{Guid.NewGuid():N}{DownloadExtension}");
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, entry.Url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new CoreException("base_models.download_failed", $"Server returned HTTP {(int)response.StatusCode} for {entry.Id}.");
            if (response.Content.Headers.ContentLength is { } length && length != entry.ByteSize)
                throw new CoreException("base_models.source_changed", $"Server reported {length} bytes for {entry.Id}, expected {entry.ByteSize}.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                // Reserving the full size reports a full disk before the download, not at its end.
                PreallocationSize = entry.ByteSize,
            }))
            {
                long received = 0;
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > entry.ByteSize)
                        throw new CoreException("base_models.source_changed", $"Server sent more than {entry.ByteSize} bytes for {entry.Id}.");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    ReportProgress(received);
                }
                if (received != entry.ByteSize)
                    throw new CoreException("base_models.download_failed", $"Download of {entry.Id} ended after {received} of {entry.ByteSize} bytes.");
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), entry.Sha256, StringComparison.Ordinal))
                throw new CoreException("base_models.integrity_failed", $"Downloaded file for {entry.Id} does not match its SHA-256.");

            cancellationToken.ThrowIfCancellationRequested();
            await fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Replaces a damaged published copy only with a verified one.
                File.Move(temporary, filePath, overwrite: true);
            }
            finally
            {
                fileGate.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDelete(temporary);
        }
    }

    private void ReportProgress(long received)
    {
        var now = Stopwatch.GetTimestamp();
        lock (gate)
        {
            progress = received * 100.0 / entry.ByteSize;
            if (Stopwatch.GetElapsedTime(lastProgressReport, now) < ProgressInterval) return;
            lastProgressReport = now;
        }
        changed(this);
    }

    private async Task<bool> MatchesCatalogAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != entry.ByteSize) return false;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(digest), entry.Sha256, StringComparison.Ordinal);
    }

    private void RequireIdle()
    {
        lock (gate)
        {
            if (installation is not null)
                throw new CoreException("base_models.installation_active", $"Installation of {entry.Id} is running.");
        }
    }

    /// <summary>Applies a result of a file check unless an installation started meanwhile and owns the state.</summary>
    private void SetIdleState(BaseModelStatus value, string? code)
    {
        lock (gate)
        {
            if (installation is not null || (status == value && errorCode == code)) return;
            status = value;
            progress = 0;
            errorCode = code;
        }
        changed(this);
    }

    private CoreException NotInstalled() => new("base_models.not_installed", $"Base model {entry.Id} is not installed.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Leftover downloads are removed when the catalogue is loaded next time.
        }
    }
}
