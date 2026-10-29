namespace MautVision.Desktop.Controllers.System.ResponseDto;

/// <summary>Accepted — false if the answer refers to a close request that is no longer current.</summary>
public sealed record CloseResponseDto(bool Accepted);

/// <summary>The app.closeRequested event: the window is closing; the UI answers system.closeDecision with this RequestId within 1 s.</summary>
public sealed record CloseRequestedEventDto(string RequestId);
