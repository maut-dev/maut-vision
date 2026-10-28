using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Desktop.Controllers.Projects.Catalog.ResponseDto;
using MautVision.Desktop.Resources;

namespace MautVision.Desktop.Controllers.Projects.Catalog;

internal static class CatalogResponseMapper
{
    internal static VariantResponseDto Map(ProjectVariant variant) => new(
        variant.Id, variant.Name, variant.Description, variant.SortOrder, variant.CreatedAt, variant.UpdatedAt, variant.ArchivedAt, variant.Revision);

    internal static ClassResponseDto Map(CatalogClass item) => new(
        item.Id, item.Name, item.Description, item.SortOrder, item.CreatedAt, item.UpdatedAt, item.ArchivedAt, item.Revision);

    internal static ClassVariantResponseDto Map(ClassVariant binding) => new(
        binding.Id,
        binding.ClassId,
        binding.VariantId,
        binding.Color,
        binding.DisplayColor,
        binding.CreatedAt,
        binding.UpdatedAt,
        binding.ArchivedAt,
        binding.Revision,
        binding.Available,
        binding.AssetCount,
        binding.OpaqueAssetCount,
        binding.MaxAssetWidth,
        binding.MaxAssetHeight);

    internal static CatalogWorkspaceResponseDto Map(CatalogWorkspace workspace) => new(
        workspace.Variants.Select(Map).ToArray(),
        workspace.Classes.Select(Map).ToArray(),
        workspace.ClassVariants.Select(Map).ToArray(),
        workspace.ClassOrderRevision,
        workspace.CatalogRevision);

    internal static ClassOrderResponseDto Map(ClassOrder order) => new(order.Classes.Select(Map).ToArray(), order.OrderRevision);

    /// <summary>URLs are issued here, for the page that asked; the bytes go through the resource channel.</summary>
    internal static ClassAssetResponseDto Map(string projectId, ClassAsset asset, ResourceHandles resources)
    {
        var reference = new ResourceReference(ClassAsset.ResourceKind, asset.Id);
        return new ClassAssetResponseDto(
            asset.Id,
            asset.ClassVariantId,
            Format(asset.Format),
            asset.Width,
            asset.Height,
            asset.HasAlpha,
            asset.ByteSize,
            asset.CreatedAt,
            resources.Issue(projectId, reference, ResourceVariant.Original),
            resources.Issue(projectId, reference, ResourceVariant.Thumbnail));
    }

    internal static ClassAssetPageResponseDto Map(string projectId, ClassAssetPage page, ResourceHandles resources)
        => new(page.Items.Select(asset => Map(projectId, asset, resources)).ToArray(), page.NextCursor);

    internal static DeletionReviewResponseDto Map(DeletionReview review) => new(
        review.TargetKind switch
        {
            CatalogTargetKind.Variant => "variant",
            CatalogTargetKind.Class => "class",
            CatalogTargetKind.ClassVariant => "classVariant",
            CatalogTargetKind.Asset => "asset",
            _ => throw new ArgumentOutOfRangeException(nameof(review), review.TargetKind, "Unknown deletion target."),
        },
        review.TargetId,
        review.TargetName,
        review.Classes,
        review.ClassVariants,
        review.Assets,
        review.Blockers.Select(blocker => DeletionBlockerResponseDto.From(blocker)).ToArray(),
        review.Revision);

    internal static DeletionResultResponseDto Map(DeletionResult result) => new(result.Classes, result.ClassVariants, result.Assets, result.CleanupPending);

    internal static CatalogChangedEventDto Map(CatalogChangedEventArgs change) => new(
        change.ProjectId,
        change.Variants.Select(Map).ToArray(),
        change.Classes.Select(Map).ToArray(),
        change.ClassVariants.Select(Map).ToArray(),
        change.AssetsChangedIn.ToArray(),
        change.ClassOrderRevision,
        change.CatalogRevision);

    private static CatalogEntityChangeDto Map(CatalogEntityChange change) => new(change.Id, change.Revision);

    private static string Format(ClassAssetFormat format) => format switch
    {
        ClassAssetFormat.Png => "png",
        ClassAssetFormat.Jpeg => "jpeg",
        ClassAssetFormat.Webp => "webp",
        ClassAssetFormat.Bmp => "bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown asset format."),
    };
}
