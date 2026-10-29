namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>
/// "Export…": opens "Save as" with the name "&lt;name&gt; &lt;YYYY-MM-DD&gt;.zip"; FallbackName — the name in the language of the page when the
/// project name has no characters a file name can keep (null — English "Project").
/// </summary>
public sealed record SelectProjectExportRequestDto(string ProjectId, string? FallbackName = null);

/// <summary>SelectionToken — the answer of selectExportDestination; OperationId — new per click (a repeat returns the same job).</summary>
public sealed record StartProjectExportRequestDto(string ProjectId, string OperationId, string SelectionToken);

/// <summary>"Import project…": opens the selection of a .zip archive.</summary>
public sealed record SelectProjectArchiveRequestDto;

/// <summary>
/// SelectionToken — the answer of selectImportArchive; OperationId — new per click (a repeat returns the same import); CopyLabel — the word
/// of the suffix " (<label>)" a copy gets when the name is taken, in the language of the page; null — English "import".
/// </summary>
public sealed record StartProjectImportRequestDto(string OperationId, string SelectionToken, string? CopyLabel = null);

public sealed record GetProjectImportRequestDto(Guid ImportId);

public sealed record ListProjectImportsRequestDto;

public sealed record CancelProjectImportRequestDto(Guid ImportId);

public sealed record ReviewProjectDeletionRequestDto(string ProjectId);

/// <summary>Revision — from reviewDeletion; CancelJobs — "Cancel jobs and delete".</summary>
public sealed record DeleteProjectRequestDto(string ProjectId, long Revision, bool CancelJobs);
