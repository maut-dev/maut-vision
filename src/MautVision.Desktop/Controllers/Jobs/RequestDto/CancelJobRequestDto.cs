namespace MautVision.Desktop.Controllers.Jobs.RequestDto;

public sealed record CancelJobRequestDto(string ProjectId, Guid JobId);
