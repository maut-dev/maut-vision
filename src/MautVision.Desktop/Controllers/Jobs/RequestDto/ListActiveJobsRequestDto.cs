namespace MautVision.Desktop.Controllers.Jobs.RequestDto;

/// <summary>Limit: 1–200, 30 by default. After: NextCursor of the previous page.</summary>
public sealed record ListActiveJobsRequestDto(string ProjectId, int? Limit = null, Guid? After = null);
