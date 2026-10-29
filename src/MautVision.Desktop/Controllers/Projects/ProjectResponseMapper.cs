using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Transfer;
using MautVision.Desktop.Controllers.Jobs.ResponseDto;
using MautVision.Desktop.Controllers.Projects.ResponseDto;

namespace MautVision.Desktop.Controllers.Projects;

internal static class ProjectResponseMapper
{
    internal static ProjectResponseDto Map(IProject project) => new(
        project.Id,
        project.Name,
        project.Description,
        project.BaseModelId,
        new InputSizeDto(project.InputSize.Width, project.InputSize.Height),
        project.Revision,
        project.CreatedAt,
        project.UpdatedAt,
        project.ArchivedAt);

    internal static ProjectImportResponseDto Map(ProjectImportInfo import) => new(
        import.Id,
        import.OperationId,
        import.FileName,
        import.Status switch
        {
            ProjectImportStatus.Running => "running",
            ProjectImportStatus.Succeeded => "succeeded",
            ProjectImportStatus.Failed => "failed",
            ProjectImportStatus.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(import), import.Status, "Unknown import status."),
        },
        import.CancelRequested,
        import.ProcessedFiles,
        import.TotalFiles,
        import.ProcessedBytes,
        import.TotalBytes,
        import.ProjectId,
        import.ProjectName,
        import.ErrorCode is { } code ? new JobErrorDto(code, null) : null,
        import.StartedAt,
        import.FinishedAt,
        import.Sequence);
}
