namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>Accepted — false, если ответ относится к уже неактуальному запросу закрытия.</summary>
public sealed record CloseResponseDto(bool Accepted);

/// <summary>Событие app.closeRequested: окно закрывается; UI отвечает system.closeDecision с этим RequestId в течение 1 с.</summary>
public sealed record CloseRequestedEventDto(string RequestId);
