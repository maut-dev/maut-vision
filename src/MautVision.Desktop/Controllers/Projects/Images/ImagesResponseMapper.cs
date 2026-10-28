using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Controllers.Projects.Images.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Images;

/// <summary>Maps images contracts to DTOs and parses their enum strings; unknown strings are refused as bridge.validation.</summary>
internal static class ImagesResponseMapper
{
    internal static ImageGroupResponseDto Map(ImageGroup group) => new(
        group.Id,
        group.Name,
        group.Description,
        group.Origin == ImageGroupOrigin.Manual ? "manual" : "generated",
        SplitKey(group.SplitHint),
        group.Revision,
        group.CreatedAt,
        group.UpdatedAt,
        group.ArchivedAt,
        group.GenerationRunId,
        group.GenerationState switch
        {
            null => null,
            ImageGenerationState.Producing => "producing",
            ImageGenerationState.Completed => "completed",
            _ => "partial",
        },
        group.ImageCount,
        group.ArchivedImageCount);

    /// <summary>URLs are issued here, for the page that asked; the bytes go through the resource channel.</summary>
    internal static ImageResponseDto Map(string projectId, ProjectImage image, ResourceHandles resources)
    {
        var reference = new ResourceReference(ProjectImage.ResourceKind, image.Id);
        var lineage = image.Lineage;
        return new ImageResponseDto(
            image.Id,
            image.GroupId,
            image.DisplayName,
            image.Format switch
            {
                ImageFileFormat.Png => "png",
                ImageFileFormat.Jpeg => "jpeg",
                ImageFileFormat.Webp => "webp",
                _ => "bmp",
            },
            image.Width,
            image.Height,
            image.ByteSize,
            image.Revision,
            image.AnnotationRevision,
            image.ObjectCount,
            ReviewKey(image.ReviewState),
            image.CreatedAt,
            image.ArchivedAt,
            new ImageLineageResponseDto(
                lineage.Kind switch { ImageLineageKind.File => "file", ImageLineageKind.Video => "video", _ => "generated" },
                lineage.SourceFamilyId,
                lineage.VideoName,
                lineage.TimestampMs,
                lineage.FrameIndex,
                lineage.GenerationRunId,
                lineage.SampleIndex,
                lineage.RecipeRevision),
            resources.Issue(projectId, reference, ResourceVariant.Original),
            resources.Issue(projectId, reference, ResourceVariant.Thumbnail));
    }

    internal static ImagePageResponseDto Map(string projectId, ImagePage page, ResourceHandles resources)
        => new(page.Items.Select(image => Map(projectId, image, resources)).ToArray(), page.NextCursor);

    internal static ImageAnnotationResponseDto Map(ImageAnnotation annotation) => new(
        annotation.ImageId,
        annotation.Revision,
        annotation.Objects.Select(item => new AnnotationObjectResponseDto(item.Id, item.ClassId, item.ClassVariantId, Map(item.Box))).ToArray(),
        annotation.Roi is { } roi ? Map(roi) : null,
        ReviewKey(annotation.ReviewState),
        annotation.CreatedAt);

    internal static ImageDeletionReviewResponseDto Map(ImageDeletionReview review) => new(
        review.GroupId,
        review.GroupName,
        review.WholeGroup,
        review.Images,
        review.Blockers.Select(blocker => new DeletionBlockerResponseDto(blocker.Code, blocker.Count, ImageErrorMessages.For(blocker))).ToArray(),
        review.Revision);

    internal static ImageDeletionResultResponseDto Map(ImageDeletionResult result) => new(result.GroupId, result.GroupDeleted, result.Images, result.CleanupPending);

    internal static ImagesChangedEventDto Map(ImagesChangedEventArgs change) => new(
        change.ProjectId,
        change.Groups.Select(item => new ImagesEntityChangeDto(item.Id, item.Revision)).ToArray(),
        change.Images.Select(item => new ImagesEntityChangeDto(item.Id, item.Revision)).ToArray(),
        change.ImagesChangedIn.ToArray());

    internal static ImageSplitHint ParseSplit(string value) => value switch
    {
        "unassigned" => ImageSplitHint.Unassigned,
        "train" => ImageSplitHint.Train,
        "validation" => ImageSplitHint.Validation,
        "test" => ImageSplitHint.Test,
        _ => throw new ArgumentException("Unknown split hint.", nameof(value)),
    };

    internal static ImageReviewState ParseReview(string value) => value switch
    {
        "unreviewed" => ImageReviewState.Unreviewed,
        "reviewed" => ImageReviewState.Reviewed,
        _ => throw new ArgumentException("Unknown review state.", nameof(value)),
    };

    internal static bool? ParseArchive(string? value) => value switch
    {
        null or "active" => false,
        "archived" => true,
        "all" => null,
        _ => throw new ArgumentException("Unknown archive filter.", nameof(value)),
    };

    private static ImageBoxResponseDto Map(ImageBox box) => new(box.X, box.Y, box.Width, box.Height);

    private static string SplitKey(ImageSplitHint value) => value switch
    {
        ImageSplitHint.Unassigned => "unassigned",
        ImageSplitHint.Train => "train",
        ImageSplitHint.Validation => "validation",
        _ => "test",
    };

    private static string ReviewKey(ImageReviewState value) => value == ImageReviewState.Reviewed ? "reviewed" : "unreviewed";
}
