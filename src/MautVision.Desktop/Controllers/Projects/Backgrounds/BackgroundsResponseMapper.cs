using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Controllers.Projects.Backgrounds.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Backgrounds;

/// <summary>Maps backgrounds contracts to DTOs and parses their enum strings; unknown strings are refused as bridge.validation.</summary>
internal static class BackgroundsResponseMapper
{
    internal static BackgroundSetResponseDto Map(BackgroundSet set) => new(
        set.Id,
        set.Name,
        set.Description,
        Map(set.TargetSize),
        set.Revision,
        set.ContentRevision,
        set.ItemCount,
        set.RemovedCount,
        set.CreatedAt,
        set.UpdatedAt,
        set.ArchivedAt,
        set.MatchesProjectInput);

    /// <summary>URLs are issued here, for the page that asked; the bytes go through the resource channel.</summary>
    internal static BackgroundResponseDto Map(string projectId, BackgroundItem item, ResourceHandles resources)
    {
        var reference = new ResourceReference(BackgroundItem.ResourceKind, item.Id);
        var origin = item.Origin;
        return new BackgroundResponseDto(
            item.Id,
            item.SetId,
            item.Width,
            item.Height,
            item.ByteSize,
            item.Revision,
            item.CreatedAt,
            item.RemovedAt,
            new BackgroundOriginResponseDto(
                OriginKey(origin.Kind),
                origin.OperationId,
                origin.SourceName,
                origin.VideoName,
                origin.TimestampMs,
                origin.FrameIndex,
                Map(origin.Crop),
                origin.BlurRadius,
                origin.ProcessorVersion),
            resources.Issue(projectId, reference, ResourceVariant.Original),
            resources.Issue(projectId, reference, ResourceVariant.Thumbnail));
    }

    internal static BackgroundPageResponseDto Map(string projectId, BackgroundPage page, ResourceHandles resources)
        => new(page.Items.Select(item => Map(projectId, item, resources)).ToArray(), page.NextCursor);

    internal static BackgroundDeletionReviewResponseDto Map(BackgroundDeletionReview review) => new(
        review.SetId,
        review.SetName,
        review.WholeSet,
        review.Items,
        review.Blockers.Select(blocker => new DeletionBlockerResponseDto(blocker.Code, blocker.Count, BackgroundErrorMessages.For(blocker))).ToArray(),
        review.Revision);

    internal static BackgroundDeletionResultResponseDto Map(BackgroundDeletionResult result) => new(result.SetId, result.SetDeleted, result.Items, result.CleanupPending);

    internal static BackgroundsChangedEventDto Map(BackgroundsChangedEventArgs change) => new(
        change.ProjectId,
        change.Sets.Select(item => new BackgroundsEntityChangeDto(item.Id, item.Revision)).ToArray(),
        change.Items.Select(item => new BackgroundsEntityChangeDto(item.Id, item.Revision)).ToArray(),
        change.ItemsChangedIn.ToArray());

    internal static BackgroundBlurResponseDto? Map(BackgroundBlur? blur) => blur is null ? null : new(blur.MinRadius, blur.MaxRadius, blur.Seed ?? 0);

    internal static BackgroundCropAreaDto? Map(BackgroundCropArea? crop) => crop is null ? null : new(crop.Left, crop.Top, crop.Width, crop.Height);

    internal static InputSizeDto Map(InputSize size) => new(size.Width, size.Height);

    internal static string FitKey(BackgroundSourceFit fit) => fit switch
    {
        BackgroundSourceFit.Exact => "exact",
        BackgroundSourceFit.Larger => "larger",
        BackgroundSourceFit.Smaller => "smaller",
        _ => "unreadable",
    };

    /// <summary>active (default), removed or all.</summary>
    internal static bool? ParseRemoved(string? value) => value switch
    {
        null or "active" => false,
        "removed" => true,
        "all" => null,
        _ => throw new ArgumentException("Unknown removed filter.", nameof(value)),
    };

    private static string OriginKey(BackgroundOriginKind kind) => kind switch
    {
        BackgroundOriginKind.Uploaded => "uploaded",
        BackgroundOriginKind.ImageCrop => "image-crop",
        _ => "video-frame",
    };
}
