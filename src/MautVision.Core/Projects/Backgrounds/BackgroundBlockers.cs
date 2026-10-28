using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>
/// Reports why backgrounds of a set cannot be deleted now (and why the set cannot be archived). itemIds — the backgrounds
/// to delete, null for the whole set (and for archiving). Providers are registered during the Core composition: an active
/// run into the set, generation presets that would lose the set or all its active backgrounds. Jobs of other sets do not block.
/// </summary>
internal delegate Task<IReadOnlyList<DeletionBlocker>> BackgroundBlockerProvider(string projectId, ProjectStore store, Guid setId,
    IReadOnlyList<Guid>? itemIds, CancellationToken cancellationToken);

internal sealed class BackgroundBlockers
{
    /// <summary>A queued or running preparation run into the set.</summary>
    internal const string ImportActive = "backgrounds.import_active";

    private readonly Lock gate = new();
    private BackgroundBlockerProvider[] providers = [];

    internal void Register(BackgroundBlockerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate) providers = [.. providers, provider];
    }

    internal async Task<IReadOnlyList<DeletionBlocker>> CollectAsync(string projectId, ProjectStore store, Guid setId, IReadOnlyList<Guid>? itemIds,
        CancellationToken cancellationToken)
    {
        BackgroundBlockerProvider[] current;
        lock (gate) current = providers;
        var result = new List<DeletionBlocker>();
        foreach (var provider in current)
        {
            result.AddRange(await provider(projectId, store, setId, itemIds, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }
}
