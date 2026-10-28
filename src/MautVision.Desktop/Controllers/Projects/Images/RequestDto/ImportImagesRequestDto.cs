namespace MautVision.Desktop.Controllers.Projects.Images.RequestDto;

/// <summary>
/// SelectionToken — from images.selectFiles, images.selectFolder or images.selectVideo; valid for 30 minutes and only on this
/// page. OperationId — created by the UI per action; a repeat with the same value returns the same job.
/// Recursive — for a folder: including subfolders. SkipDuplicates — skip duplicates in the group (default yes).
/// StartMs, IntervalMs, Count — for a video (required): frames at marks StartMs + i × IntervalMs; start ≥ 0,
/// interval ≥ 1 ms, count ≥ 1, the last mark before the end of the video. Not passed for files and a folder.
/// </summary>
public sealed record ImportImagesRequestDto(
    string ProjectId,
    Guid GroupId,
    string SelectionToken,
    string OperationId,
    bool Recursive = false,
    bool SkipDuplicates = true,
    long? StartMs = null,
    long? IntervalMs = null,
    int? Count = null);
