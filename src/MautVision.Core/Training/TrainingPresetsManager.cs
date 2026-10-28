using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Training;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Training;

/// <summary>
/// Training presets of the work directory (training/presets.json): global, because good parameters depend on the hardware, not on
/// a project. The file is read on first use and kept in memory; every change is written to a temporary file and renamed.
/// </summary>
internal sealed class TrainingPresetsManager(string workDirectory, ILogger logger) : ITrainingPresetsManager
{
    internal const int MaxNameLength = 80;
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private List<TrainingPreset>? presets;

    public event EventHandler<TrainingPresetsChangedEventArgs>? PresetsChanged;

    private string FilePath => Path.Combine(workDirectory, "training", "presets.json");

    public async Task<IReadOnlyList<TrainingPreset>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Ordered(await LoadAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<TrainingPreset> SaveAsync(string name, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.Any(char.IsControl))
            throw new CoreException("training.preset_name_invalid", "A preset name must have 1–80 characters without control characters.");
        var validated = TrainingParameterRules.Validate(parameters);
        TrainingPreset saved;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = Now();
            var index = current.FindIndex(preset => string.Equals(preset.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
            saved = index < 0
                ? new TrainingPreset(Guid.NewGuid(), trimmed, validated, now, now)
                : current[index] with { Name = trimmed, Parameters = validated, UpdatedAt = now };
            var next = new List<TrainingPreset>(current);
            if (index < 0) next.Add(saved);
            else next[index] = saved;
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            presets = next;
        }
        finally
        {
            gate.Release();
        }
        logger.LogInformation("Training preset {PresetId} saved.", saved.Id);
        Raise(saved.Id);
        return saved;
    }

    public async Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Any(preset => preset.Id == presetId))
                throw new CoreException("training.preset_not_found", $"Training preset {presetId} does not exist.");
            var next = current.Where(preset => preset.Id != presetId).ToList();
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            presets = next;
        }
        finally
        {
            gate.Release();
        }
        logger.LogInformation("Training preset {PresetId} deleted.", presetId);
        Raise(presetId);
    }

    private async Task<List<TrainingPreset>> LoadAsync(CancellationToken cancellationToken)
    {
        if (presets is not null) return presets;
        if (!File.Exists(FilePath))
        {
            presets = [];
            return presets;
        }
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 14, FileOptions.Asynchronous);
        StoredPresets? stored;
        try
        {
            stored = await JsonSerializer.DeserializeAsync<StoredPresets>(stream, Json, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("training/presets.json cannot be read.", error);
        }
        if (stored is null || stored.FormatVersion != FormatVersion)
            throw new InvalidDataException($"training/presets.json has an unknown format version {stored?.FormatVersion}.");
        // A stored value that this version no longer accepts drops the key: the default applies, the rest of the preset stays.
        presets = stored.Presets.Select(item => new TrainingPreset(item.Id, item.Name,
            item.Parameters.Where(pair => TrainingParameterRules.Keys.Contains(pair.Key) && Accepts(pair.Key, pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            item.CreatedAt, item.UpdatedAt)).ToList();
        return presets;
    }

    private static bool Accepts(string key, JsonElement value)
    {
        try
        {
            TrainingParameterRules.Validate(new Dictionary<string, JsonElement> { [key] = value });
            return true;
        }
        catch (CoreException)
        {
            return false;
        }
    }

    private async Task WriteAsync(List<TrainingPreset> next, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".presets.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 14, FileOptions.Asynchronous))
            {
                var stored = new StoredPresets(FormatVersion, next.Select(preset => new StoredPreset(preset.Id, preset.Name,
                    new Dictionary<string, JsonElement>(preset.Parameters), preset.CreatedAt, preset.UpdatedAt)).ToList());
                await JsonSerializer.SerializeAsync(stream, stored, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Raise(Guid presetId)
    {
        if (PresetsChanged is not { } handlers) return;
        var args = new TrainingPresetsChangedEventArgs([presetId]);
        foreach (EventHandler<TrainingPresetsChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Training preset observer failed.");
            }
        }
    }

    private static List<TrainingPreset> Ordered(List<TrainingPreset> items)
        => items.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(preset => preset.Id).ToList();

    private static DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed record StoredPresets(int FormatVersion, List<StoredPreset> Presets);

    private sealed record StoredPreset(Guid Id, string Name, Dictionary<string, JsonElement> Parameters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
}
