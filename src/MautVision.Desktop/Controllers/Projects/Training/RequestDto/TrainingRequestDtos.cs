using System.Text.Json;

namespace MautVision.Desktop.Controllers.Projects.Training.RequestDto;

/// <summary>Пресеты общие для всех проектов: projectId не нужен.</summary>
public sealed record ListTrainingPresetsRequestDto;

/// <summary>«Сохранить как пресет…»: Parameters — ключи протокола worker (epochs, batch, …); то же название перезаписывает пресет.</summary>
public sealed record SaveTrainingPresetRequestDto(string Name, Dictionary<string, JsonElement> Parameters);

/// <summary>«Удалить пресет».</summary>
public sealed record DeleteTrainingPresetRequestDto(Guid PresetId);

/// <summary>Видеокарты для поля «Устройство»; Refresh — опросить nvidia-smi заново.</summary>
public sealed record ListGpusRequestDto(bool? Refresh = null);

/// <summary>Состояние среды Python worker.</summary>
public sealed record GetTrainingRuntimeRequestDto;

/// <summary>
/// «Начать обучение» / «Поставить в очередь»: версия датасета, параметры (отсутствующий ключ — подобранное значение), пресет для
/// паспорта; OperationId — новый на действие (повтор вернёт тот же запуск).
/// </summary>
public sealed record StartTrainingRunRequestDto(string ProjectId, Guid DatasetVersionId, Dictionary<string, JsonElement> Parameters, string? PresetName,
    string OperationId);

/// <summary>«Отменить обучение»: идемпотентно; запуск в очереди отменяется сразу, выполняемый — после остановки worker.</summary>
public sealed record CancelTrainingRunRequestDto(string ProjectId, Guid RunId);

/// <summary>Запуски, новые первыми; Limit 1–200 (по умолчанию 50), Before — ID последнего запуска предыдущей страницы.</summary>
public sealed record ListTrainingRunsRequestDto(string ProjectId, int? Limit = null, Guid? Before = null);

public sealed record GetTrainingRunRequestDto(string ProjectId, Guid RunId);

/// <summary>История эпох по возрастанию; After — номер последней полученной эпохи, Limit 1–1000 (по умолчанию 500).</summary>
public sealed record ListTrainingEpochsRequestDto(string ProjectId, Guid RunId, int? After = null, int? Limit = null);

/// <summary>Журнал по возрастанию; After — sequence последней полученной записи, Limit 1–1000 (по умолчанию 200).</summary>
public sealed record ListTrainingLogRequestDto(string ProjectId, Guid RunId, long? After = null, int? Limit = null);

/// <summary>Последние записи журнала; Limit 1–1000 (по умолчанию 50).</summary>
public sealed record TailTrainingLogRequestDto(string ProjectId, Guid RunId, int? Limit = null);

public sealed record ListModelVersionsRequestDto(string ProjectId);

public sealed record GetModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Сделать рабочей».</summary>
public sealed record SetWorkingModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>Состав удаления версии для общего диалога удаления.</summary>
public sealed record ReviewModelVersionDeletionRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Удалить версию»: рабочую и проверяемую или выгружаемую сейчас версию удалить нельзя (training.version_in_use).</summary>
public sealed record DeleteModelVersionRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Выгрузить ZIP», шаг 1: «Сохранить как» с именем model-vN.zip; путь остаётся в host.</summary>
public sealed record SelectModelExportRequestDto(string ProjectId, Guid VersionId);

/// <summary>«Выгрузить ZIP», шаг 2: DestinationToken — из selectModelExportDestination; OperationId — новый на действие.</summary>
public sealed record StartModelExportRequestDto(string ProjectId, Guid VersionId, string DestinationToken, string OperationId);
