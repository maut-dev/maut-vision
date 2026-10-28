namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>«Экспортировать…»: открывает «Сохранить как» с именем «&lt;имя&gt; &lt;YYYY-MM-DD&gt;.zip».</summary>
public sealed record SelectProjectExportRequestDto(string ProjectId);

/// <summary>SelectionToken — ответ selectExportDestination; OperationId — новый на каждое нажатие (повтор вернёт ту же задачу).</summary>
public sealed record StartProjectExportRequestDto(string ProjectId, string OperationId, string SelectionToken);

/// <summary>«Импортировать проект…»: открывает выбор архива .zip.</summary>
public sealed record SelectProjectArchiveRequestDto;

/// <summary>SelectionToken — ответ selectImportArchive; OperationId — новый на каждое нажатие (повтор вернёт тот же импорт).</summary>
public sealed record StartProjectImportRequestDto(string OperationId, string SelectionToken);

public sealed record GetProjectImportRequestDto(Guid ImportId);

public sealed record ListProjectImportsRequestDto;

public sealed record CancelProjectImportRequestDto(Guid ImportId);

public sealed record ReviewProjectDeletionRequestDto(string ProjectId);

/// <summary>Revision — из reviewDeletion; CancelJobs — «Отменить задачи и удалить».</summary>
public sealed record DeleteProjectRequestDto(string ProjectId, long Revision, bool CancelJobs);
