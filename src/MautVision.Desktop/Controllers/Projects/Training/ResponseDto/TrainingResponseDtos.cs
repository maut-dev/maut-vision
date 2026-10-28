using System.Text.Json;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Datasets.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects.Training.ResponseDto;

public sealed record TrainingPresetResponseDto(Guid Id, string Name, Dictionary<string, JsonElement> Parameters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record TrainingPresetDeletionResponseDto(Guid PresetId);

/// <summary>Видеокарта: «Видеокарта {Index} — {Name}, {MemoryTotalMb / 1024} ГБ»; device = String(Index).</summary>
public sealed record GpuResponseDto(int Index, string Name, long MemoryTotalMb, string DriverVersion);

/// <summary>DriverAvailable = false — драйвера нет: только «Процессор» и ручной ввод устройства.</summary>
public sealed record GpuInventoryResponseDto(bool DriverAvailable, GpuResponseDto[] Gpus, DateTimeOffset CheckedAt);

/// <summary>Mode — packaged | interpreter; Code и Message — почему среда недоступна (null, если доступна).</summary>
public sealed record TrainingRuntimeResponseDto(bool Configured, bool Available, string? Mode, string? Code, string? Message);

/// <summary>Датасет запуска и версии: версия, название на момент запуска, hash manifest, классы 0..n−1, кадры по частям.</summary>
public sealed record TrainingDatasetDto(Guid VersionId, string Name, string ManifestHash, DatasetClassEntryDto[] Classes, DatasetSplitCountsDto Counts);

/// <summary>Ход: стадия worker, «Эпоха X из Y», «Пакет a из b», живые метрики последней эпохи (потери и качество).</summary>
public sealed record TrainingRunProgressDto(string? Stage, int? Epoch, int? Epochs, int? Batch, int? Batches, Dictionary<string, double> Metrics);

/// <summary>Ошибка: для training.worker_* — сообщение самого worker, для остальных кодов — текст приложения.</summary>
public sealed record TrainingRunErrorDto(string Code, string Message);

/// <summary>
/// Запуск: State — queued «Ожидает видеокарту» | preparing «Подготовка» | training «Обучение» | cancelling «Отмена…» | succeeded «Готово» |
/// failed «Ошибка» | cancelled «Отменено» | interrupted «Прервано»; Job — снимок задачи training.run (прогресс 0–100, «Эпоха X из Y»);
/// Parameters — запрошенные (все ключи), EffectiveParams — фактические (null, пока не пришли); Environment — окружение worker;
/// EpochCount и LogCount — записей истории и журнала; ResultVersionId — версия модели после «Готово»; Revision — для сверки с training.changed.
/// </summary>
public sealed record TrainingRunResponseDto(
    Guid Id,
    JobResponseDto Job,
    string State,
    TrainingDatasetDto Dataset,
    string Architecture,
    InputSizeDto Input,
    int ImageSize,
    Dictionary<string, JsonElement> Parameters,
    string? PresetName,
    JsonElement? EffectiveParams,
    TrainingRunProgressDto Progress,
    int EpochCount,
    long LogCount,
    JsonElement? Environment,
    TrainingRunErrorDto? Error,
    Guid? ResultVersionId,
    long Revision);

public sealed record TrainingRunPageResponseDto(TrainingRunResponseDto[] Items, long Total);

/// <summary>Эпоха: losses — box_loss, cls_loss, dfl_loss; metrics — precision, recall, mAP50, mAP50_95, valBoxLoss, valClsLoss, valDflLoss.</summary>
public sealed record TrainingEpochDto(int Epoch, Dictionary<string, double> Losses, Dictionary<string, double> Metrics, double? Lr, double? Fitness);

public sealed record TrainingEpochPageResponseDto(TrainingEpochDto[] Items, int Total, int? NextAfter);

/// <summary>Kind — log (консоль обучения) | stage | warning | system.</summary>
public sealed record TrainingLogEntryDto(long Sequence, DateTimeOffset At, string Kind, string Message);

public sealed record TrainingLogPageResponseDto(TrainingLogEntryDto[] Items, long Total, long? NextAfter);

/// <summary>Класс выхода модели; ClassId = null у объединённого класса singleCls, SourceClassIds — классы проекта, которые он представляет.</summary>
public sealed record ModelClassDto(int Index, Guid? ClassId, string Name, Guid[] SourceClassIds);

/// <summary>Файл версии: Kind — weights_pt | onnx | plot | log; Name — путь в папке версии; Url — у картинок (канал ресурсов).</summary>
public sealed record ModelVersionFileDto(string Kind, string Name, long ByteSize, string Sha256, string? Url);

/// <summary>Краткий паспорт версии модели (экран «Модели»).</summary>
public sealed record ModelVersionResponseDto(
    Guid Id,
    int Number,
    Guid RunId,
    TrainingDatasetDto Dataset,
    string Architecture,
    InputSizeDto Input,
    int ImageSize,
    Dictionary<string, JsonElement> Parameters,
    JsonElement EffectiveParams,
    int EpochsCompleted,
    bool StoppedEarly,
    int? BestEpoch,
    ModelClassDto[] Classes,
    Dictionary<string, double> Metrics,
    ModelVersionFileDto[] Files,
    string[] Notes,
    JsonElement Environment,
    bool IsWorking,
    long Revision,
    DateTimeOffset CreatedAt);

/// <summary>
/// Состав удаления версии: Evaluations — её оценок на датасетах (удаляются вместе с ней), Bytes — размер файлов; непустой Blockers —
/// удаление недоступно (training.version_working, checks.evaluation_active, checks.live_active, training.export_active; строки — Message).
/// </summary>
public sealed record ModelVersionDeletionReviewResponseDto(Guid VersionId, int Number, bool IsWorking, int Evaluations, long Bytes,
    Catalog.ResponseDto.DeletionBlockerResponseDto[] Blockers);

/// <summary>Версия удалена; CleanupPending — папка файлов ещё удаляется в фоне.</summary>
public sealed record ModelVersionDeletionResponseDto(Guid VersionId, int Number, int Evaluations, bool CleanupPending);

/// <summary>Token — выбор места сохранения (null — окно закрыто), FileName — имя выбранного файла.</summary>
public sealed record ModelExportSelectionResponseDto(string? Token, string? FileName);

/// <summary>Итог задачи training.export (job.result): «Версия N выгружена: model-vN.zip · 1,2 МБ».</summary>
public sealed record ModelExportResultDto(Guid VersionId, int VersionNumber, string FileName, long Bytes, string? Sha256);

/// <summary>Итог задачи training.run (job.result): созданная версия.</summary>
public sealed record TrainingRunResultDto(Guid VersionId);

public sealed record TrainingEntityChangeDto(Guid Id, long? Revision);

/// <summary>training.changed: запуски и версии с новыми ревизиями (перечитать getRun / getModelVersion), без данных.</summary>
public sealed record TrainingChangedEventDto(string ProjectId, TrainingEntityChangeDto[] Runs, TrainingEntityChangeDto[] Versions);

/// <summary>trainingPresets.changed: пресеты сохранены или удалены — перечитать listPresets.</summary>
public sealed record TrainingPresetsChangedEventDto(Guid[] PresetIds);
