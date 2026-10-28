namespace MautVision.Desktop.Controllers.Jobs.RequestDto;

/// <summary>Limit: 1–200, 50 by default. Before: ID of the last job of the previous page.</summary>
public sealed record ListJobsRequestDto(string ProjectId, int? Limit = null, Guid? Before = null);
