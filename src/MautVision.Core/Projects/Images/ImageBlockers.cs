using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Images;

/// <summary>
/// Reports why images of a group cannot be deleted now (and why the group cannot be archived). Providers are registered
/// during the Core composition: an active import into the group, a generation filling it. Jobs of other groups do not block.
/// </summary>
internal delegate Task<IReadOnlyList<DeletionBlocker>> ImageBlockerProvider(string projectId, ProjectStore store, Guid groupId,
    CancellationToken cancellationToken);

internal sealed class ImageBlockers
{
    /// <summary>A queued or running import into the group.</summary>
    internal const string ImportActive = "images.import_active";

    /// <summary>A generation run is filling the group (its state is producing); the example is the group name.</summary>
    internal const string GenerationActive = "images.generation_active";

    private readonly Lock gate = new();
    private ImageBlockerProvider[] providers = [];

    internal void Register(ImageBlockerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate) providers = [.. providers, provider];
    }

    internal async Task<IReadOnlyList<DeletionBlocker>> CollectAsync(string projectId, ProjectStore store, Guid groupId, CancellationToken cancellationToken)
    {
        ImageBlockerProvider[] current;
        lock (gate) current = providers;
        var result = new List<DeletionBlocker>();
        foreach (var provider in current)
        {
            result.AddRange(await provider(projectId, store, groupId, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }
}
