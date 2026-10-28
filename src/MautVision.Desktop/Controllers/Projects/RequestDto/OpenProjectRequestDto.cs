namespace MautVision.Desktop.Controllers.Projects.RequestDto;

/// <summary>
/// Sequence — номер перехода, растущий в UI; если пока проект открывался, пришло открытие с большим номером, ответ —
/// <c>projects.open_superseded</c> (результат устарел). Без номера ответ не сравнивается.
/// </summary>
public sealed record OpenProjectRequestDto(string ProjectId, long? Sequence = null);
