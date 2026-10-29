namespace MautVision.Desktop.Controllers.System.RequestDto;

/// <summary>UI answer to the app.closeRequested event: RequestId from the event, Allow — the window may close (no unsaved changes).</summary>
public sealed record CloseDecisionRequestDto(string RequestId, bool Allow);
