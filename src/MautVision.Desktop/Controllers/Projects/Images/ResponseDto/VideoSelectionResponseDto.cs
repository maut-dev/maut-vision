namespace MautVision.Desktop.Controllers.Projects.Images.ResponseDto;

/// <summary>
/// Video selected in a native dialog. SelectionToken — null if the user closed the dialog (then the other fields are null);
/// valid for 30 minutes and only on this page. DisplayName — file name without path, ByteSize — file size,
/// Width/Height — frame size with rotation applied, DurationMs — duration. The video is not hashed on selection.
/// </summary>
public sealed record VideoSelectionResponseDto(string? SelectionToken, string? DisplayName, long? ByteSize, int? Width, int? Height, long? DurationMs);
