using System.Text.Json;

namespace MautVision.Core.Interfaces.Training;

/// <summary>
/// Пресет параметров обучения: общий для всех проектов (параметры зависят от железа), хранится в рабочей директории.
/// Parameters — ключи протокола worker в camelCase (epochs, batch, device, lr0, …) и их JSON-значения; отсутствующий ключ —
/// подобранное значение по умолчанию.
/// </summary>
public sealed record TrainingPreset(Guid Id, string Name, IReadOnlyDictionary<string, JsonElement> Parameters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Пресеты изменились (сохранён, перезаписан или удалён пресет).</summary>
public sealed class TrainingPresetsChangedEventArgs(IReadOnlyList<Guid> presetIds) : EventArgs
{
    public IReadOnlyList<Guid> PresetIds { get; } = presetIds;
}

/// <summary>
/// Пресеты параметров обучения. Название уникально без учёта регистра; сохранение с существующим названием перезаписывает
/// пресет (тот же ID). Неверный параметр — <c>training.parameter_invalid</c> (Details["field"] — ключ), название —
/// <c>training.preset_name_invalid</c>, отсутствующий пресет — <c>training.preset_not_found</c>.
/// </summary>
public interface ITrainingPresetsManager
{
    event EventHandler<TrainingPresetsChangedEventArgs>? PresetsChanged;

    /// <summary>Все пресеты по названию.</summary>
    Task<IReadOnlyList<TrainingPreset>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>«Сохранить как пресет…»: новый пресет или перезапись пресета с тем же названием; параметры проверяются как у запуска.</summary>
    Task<TrainingPreset> SaveAsync(string name, IReadOnlyDictionary<string, JsonElement> parameters, CancellationToken cancellationToken = default);

    /// <summary>«Удалить пресет».</summary>
    Task DeleteAsync(Guid presetId, CancellationToken cancellationToken = default);
}
