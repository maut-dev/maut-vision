using System.Text.Json;

namespace MautVision.Core.Interfaces.Training;

/// <summary>
/// Training parameter preset: shared by all projects (the parameters depend on the hardware), stored in the working directory.
/// Parameters — worker protocol keys in camelCase (epochs, batch, device, lr0, …) and their JSON values; a missing key —
/// the chosen default value.
/// </summary>
public sealed record TrainingPreset(Guid Id, string Name, IReadOnlyDictionary<string, JsonElement> Parameters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Presets changed (a preset was saved, overwritten or deleted).</summary>
public sealed class TrainingPresetsChangedEventArgs(IReadOnlyList<Guid> presetIds) : EventArgs
{
    public IReadOnlyList<Guid> PresetIds { get; } = presetIds;
}

/// <summary>
/// Training parameter presets. The name is unique case-insensitively; saving with an existing name overwrites
/// the preset (the same ID). An invalid parameter — <c>training.parameter_invalid</c> (Details["field"] — the key), name —
/// <c>training.preset_name_invalid</c>, a missing preset — <c>training.preset_not_found</c>.
/// </summary>
public interface ITrainingPresetsManager
{
    event EventHandler<TrainingPresetsChangedEventArgs>? PresetsChanged;

    /// <summary>All presets by name.</summary>
    Task<IReadOnlyList<TrainingPreset>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>"Save as preset…": a new preset or an overwrite of the preset with the same name; parameters are validated as for a run.</summary>
    Task<TrainingPreset> SaveAsync(string name, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken cancellationToken = default);

    /// <summary>"Delete preset".</summary>
    Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every supported parameter key with its tuned default, in registry order; null — the worker computes the value (lr0, warmupEpochs,
    /// patience, workers) or the setting is off.
    /// </summary>
    IReadOnlyDictionary<string, JsonElement> Defaults { get; }
}
