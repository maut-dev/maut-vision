namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>
/// Sequence — transition number growing in the UI; if an open with a greater number arrived while the project was opening, the answer is
/// <c>projects.open_superseded</c> (the result is stale). Without a number the answer is not compared.
/// </summary>
public sealed record OpenProjectRequestDto(string ProjectId, long? Sequence = null);
