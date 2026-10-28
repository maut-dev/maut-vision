namespace MautVision.Desktop.Controllers.System.RequestDto;

/// <summary>Ответ UI на событие app.closeRequested: RequestId из события, Allow — можно закрыть окно (нет несохранённых изменений).</summary>
public sealed record CloseDecisionRequestDto(string RequestId, bool Allow);
