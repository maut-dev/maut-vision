using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Media;

namespace MautVision.Core.Interfaces.Projects.Images;

/// <summary>
/// Import source kind. A new kind (video frames) is added as a value and optional source fields,
/// without changing the rest of the job input.
/// </summary>
public enum ImageImportSourceKind
{
    /// <summary>A single PNG, JPEG, WebP or BMP file.</summary>
    File,

    /// <summary>Folder: files with these extensions, by relative path in natural order; links are not followed.</summary>
    Folder,

    /// <summary>
    /// Video frames (MP4, MKV, AVI, MOV, WebM, M4V) at the marks <see cref="ImageImportSource.Frames"/>: PNG of the original size
    /// named "&lt;video name&gt;-frame-&lt;mark, ms&gt;.png" with origin video.
    /// </summary>
    Video,
}

/// <summary>
/// Import source. Path — absolute path; stored in the job input in project.db and never leaves it.
/// Recursive — for a folder: including subfolders. Frames — for a video: frame marks (required).
/// </summary>
public sealed record ImageImportSource(ImageImportSourceKind Kind, string Path, bool Recursive = false, VideoFrameRange? Frames = null);

/// <summary>
/// Input of the job that imports into a manual group. The frame order in the group is the source order, inside a folder — by relative
/// path. SkipDuplicates — skip frames whose pixels (PixelHash) are already in the group.
/// </summary>
public sealed record ImportImagesInput(Guid GroupId, IReadOnlyList<ImageImportSource> Sources, bool SkipDuplicates = true);

/// <summary>
/// Import result: Total — files (frames) after expanding folders and videos, Added, Duplicates (skipped duplicates), Rejected;
/// RejectedFiles — the first <see cref="MaxRejectedFiles"/> rejected ones (name or relative path in the folder, reason code
/// <c>images.file_invalid</c>, <c>images.file_unreadable</c> or <c>video.frame_unavailable</c> — the video ended before the mark).
/// </summary>
public sealed record ImportImagesResult(int Total, int Added, int Duplicates, int Rejected, IReadOnlyList<ImportRejectedFile> RejectedFiles)
{
    public const int MaxRejectedFiles = 50;
}

/// <summary>
/// Import of images into a group (CPU, resumable). Each file is fully decoded once: size with
/// EXIF orientation applied, pixel check, PixelHash; the file is copied unchanged. The group revision is not required. A group that is not
/// manual — <c>images.group_not_manual</c>, archived or deleted — <c>images.group_archived</c>, <c>images.group_not_found</c>;
/// folder unavailable — <c>images.folder_unavailable</c>; the content of folders or videos changed before resuming after a restart —
/// <c>images.import_sources_changed</c>. Video: no ffmpeg — <c>video.ffmpeg_missing</c>, the file is unreadable —
/// <c>video.unreadable</c> or <c>video.unsupported</c>, invalid marks — <c>video.frames_invalid</c>, the last one not before
/// the end — <c>video.range_invalid</c>. Frames of one video are decoded by one ffmpeg process; the family hash (SHA-256 of the file)
/// is computed in the job. Cancellation and failure keep what was added; the result is in <see cref="JobInfo.Result"/>.
/// </summary>
[JobKind("images.import")]
public interface IImportImagesJob : IJobDefinition<ImportImagesInput>
{
}
