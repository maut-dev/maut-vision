using System.Text.Json;
using MautVision.Core.Interfaces.Jobs;

namespace MautVision.Core.Interfaces.Projects.Training;

/// <summary>
/// Вход запуска обучения, который готовит <see cref="ITrainingManager.PrepareRunAsync"/>: датасет (DatasetVersionId — ключ
/// блокировки удаления), модель и вход проекта на момент запуска, полные запрошенные параметры и пресет.
/// </summary>
public sealed record TrainingRunInput(
    Guid DatasetVersionId,
    TrainingDatasetReference Dataset,
    string Architecture,
    int Width,
    int Height,
    int ImageSize,
    IReadOnlyDictionary<string, JsonElement> Parameters,
    string? PresetName);

/// <summary>
/// Запуск обучения: GPU-задача (одна GPU-задача одновременно, очередь сохраняется между запусками), не возобновляемая —
/// после завершения приложения опубликованный результат принимается при открытии проекта, иначе запуск «Прерван».
/// Ставится ICore.Jobs.EnqueueAsync с operationId запроса; JobInfo.Progress — эпохи и пакеты, ProgressText — «Эпоха X из Y».
/// </summary>
[JobKind("training.run")]
public interface ITrainingRunJob : IJobDefinition<TrainingRunInput>
{
}

/// <summary>Итог успешного запуска (JobInfo.Result): созданная версия модели.</summary>
public sealed record TrainingRunResult(Guid VersionId);

/// <summary>Вход выгрузки ZIP версии модели: версия, её номер и абсолютный путь архива, выбранный в «Сохранить как».</summary>
public sealed record ModelExportInput(Guid VersionId, int VersionNumber, string DestinationPath);

/// <summary>
/// Выгрузка версии в ZIP (CPU, не возобновляемая): weights/best.pt и weights/best.onnx (если есть) со сверкой SHA-256 и размера
/// (<c>training.artifact_corrupt</c> при расхождении), manifest.json, classes.json, README.md. Архив воспроизводим: фиксированная
/// дата записей и порядок. Пишется во временный файл рядом с целевым и заменяет его в конце; отмена и ошибка удаляют временный
/// файл. Прогресс — байты весов; итог — <see cref="ModelExportResult"/>.
/// </summary>
[JobKind("training.export")]
public interface IModelExportJob : IJobDefinition<ModelExportInput>
{
}

/// <summary>Итог выгрузки: «Версия N выгружена: model-vN.zip · 1,2 МБ»; Sha256 — архива целиком.</summary>
public sealed record ModelExportResult(Guid VersionId, int VersionNumber, string FileName, long Bytes, string? Sha256);
