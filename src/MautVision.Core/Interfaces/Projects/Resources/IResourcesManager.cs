namespace MautVision.Core.Interfaces.Projects.Resources;

/// <summary>
/// Project material files for display in the UI. Paths are built only from the material kind and ID; thumbnails are created
/// on the first request and cached in the project folder. The cache can be deleted at any time.
/// </summary>
public interface IResourcesManager
{
    /// <summary>
    /// Opens a resource file; opens the project storage if it is not open yet. Errors: <c>resources.kind_unknown</c>,
    /// <c>resources.not_found</c>, <c>resources.thumbnail_unsupported</c> (thumbnail of a non-image), <c>resources.decode_failed</c>.
    /// Cancellation stops waiting; a thumbnail generation already started completes and stays in the cache.
    /// </summary>
    Task<ResourceContent> OpenAsync(ResourceReference reference, ResourceVariant variant, CancellationToken cancellationToken = default);
}
