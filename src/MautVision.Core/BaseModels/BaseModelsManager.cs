using System.Net;
using MautVision.Core.Interfaces.BaseModels;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.BaseModels;

internal sealed class BaseModelsManager : IBaseModelsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
    };

    // One client for the process: downloads reuse connections, and a stalled body is limited per read in BaseModel.
    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private List<IBaseModel> baseModels = [];
    private bool isLoaded;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly string catalogPath;
    private readonly string weightsDirectory;
    private readonly HttpClient http;
    private readonly ILogger logger;

    internal BaseModelsManager(string catalogPath, string baseModelsDirectory, ILogger logger)
        : this(catalogPath, baseModelsDirectory, SharedHttp, logger)
    {
    }

    internal BaseModelsManager(string catalogPath, string baseModelsDirectory, HttpClient http, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseModelsDirectory);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(logger);
        this.catalogPath = Path.GetFullPath(catalogPath);
        weightsDirectory = Path.Combine(Path.GetFullPath(baseModelsDirectory), "weights");
        this.http = http;
        this.logger = logger;
    }

    public event EventHandler<BaseModelChangedEventArgs>? ModelChanged;

    private async Task Initialize(CancellationToken cancellationToken)
    {
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (isLoaded) return;

            await using var file = new FileStream(catalogPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            List<BaseModelCatalogEntry?> entries;
            try
            {
                entries = await JsonSerializer.DeserializeAsync<List<BaseModelCatalogEntry?>>(file, JsonOptions, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidDataException("The model catalog must contain a JSON array, not null.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Failed to parse yolo-catalog.json: an array of model entries is expected.", error);
            }

            var models = new List<IBaseModel>(entries.Count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id))
                    throw new InvalidDataException("Every catalog entry must have a non-empty id.");
                if (!ids.Add(entry.Id))
                    throw new InvalidDataException($"The catalog repeats the model id: {entry.Id}.");
                Validate(entry);
                if (!fileNames.Add(entry.FileName))
                    throw new InvalidDataException($"The catalog repeats the file name: {entry.FileName}.");
                var model = new BaseModel(entry, weightsDirectory, http, OnModelChanged, logger);
                model.InspectStoredFile();
                models.Add(model);
            }
            RemoveUnfinishedDownloads();

            // Publish only a completely parsed catalogue. A failed load can be retried.
            baseModels = models;
            isLoaded = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "Base model catalogue {Catalog} failed to load.", catalogPath);
            throw;
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<IReadOnlyList<IBaseModel>> ListAsync(CancellationToken cancellationToken = default)
    {
        await Initialize(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(baseModels.ToArray());
    }

    public async Task<IBaseModel?> GetOrNullAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await Initialize(cancellationToken).ConfigureAwait(false);
        return baseModels.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.Ordinal));
    }

    private void OnModelChanged(BaseModel model)
    {
        var subscribers = ModelChanged;
        if (subscribers is null) return;
        var args = new BaseModelChangedEventArgs(model);
        foreach (EventHandler<BaseModelChangedEventArgs> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(this, args);
            }
            catch (Exception)
            {
                // Observers cannot break an installation or its state transition.
            }
        }
    }

    /// <summary>Downloads interrupted by an application exit leave temporary files; no installation runs yet.</summary>
    private void RemoveUnfinishedDownloads()
    {
        if (!Directory.Exists(weightsDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(weightsDirectory, "*" + BaseModel.DownloadExtension))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A locked leftover does not prevent using the catalogue; it is retried on the next start.
                logger.LogWarning(error, "Interrupted download {File} could not be removed.", Path.GetFileName(path));
            }
        }
    }

    private static void Validate(BaseModelCatalogEntry entry)
    {
        var fileName = entry.FileName;
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) || fileName.StartsWith('.')
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.EndsWith(BaseModel.DownloadExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Invalid model file name {entry.Id}.");
        if (entry.Stride is not (32 or 64))
            throw new InvalidDataException($"The input stride of model {entry.Id} must be 32 or 64.");
        if (entry.ByteSize <= 0)
            throw new InvalidDataException($"The size of model {entry.Id} must be positive.");
        if (entry.Sha256.Length != 64 || !entry.Sha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException($"The SHA-256 of model {entry.Id} must consist of 64 lowercase hexadecimal characters.");
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException($"The URL of model {entry.Id} must be an absolute HTTPS URL.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MautVision/0.1");
        return client;
    }
}
