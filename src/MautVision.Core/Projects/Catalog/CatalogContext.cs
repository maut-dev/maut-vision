using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Projects.Catalog;

/// <summary>
/// What the catalog managers of one project share: its store, the open state, deletion, the change event and the
/// project's file cleanup queue.
/// </summary>
internal sealed class CatalogContext
{
    private readonly Func<bool> isOpen;
    private readonly Action<CatalogChangedEventArgs> changed;

    internal CatalogContext(string projectId, ProjectStore store, CatalogBlockers blockers, Func<bool> isOpen,
        Action<CatalogChangedEventArgs> changed, FileCleanup cleanup, ILogger logger)
    {
        ProjectId = projectId;
        Cleanup = cleanup;
        Store = store;
        this.isOpen = isOpen;
        this.changed = changed;
        Logger = logger;
        Deletion = new CatalogDeletion(this, blockers);
    }

    internal string ProjectId { get; }
    internal ProjectStore Store { get; }
    internal ILogger Logger { get; }
    internal FileCleanup Cleanup { get; }
    internal CatalogDeletion Deletion { get; }

    /// <summary>The catalog lives in project.db, which is prepared by IProjectsManager.OpenAsync; before that calls are refused.</summary>
    internal void RequireOpen(string area)
    {
        if (!isOpen())
            throw new CoreException($"{area}.project_not_open", $"Project {ProjectId} is not open; its catalog is not available.");
    }

    internal void Raise(
        long orderRevision,
        long catalogRevision,
        IReadOnlyList<CatalogEntityChange>? variants = null,
        IReadOnlyList<CatalogEntityChange>? classes = null,
        IReadOnlyList<CatalogEntityChange>? classVariants = null,
        IReadOnlyList<Guid>? assetsChangedIn = null)
        => changed(new CatalogChangedEventArgs(ProjectId, variants ?? [], classes ?? [], classVariants ?? [], assetsChangedIn ?? [],
            orderRevision, catalogRevision));
}

/// <summary>
/// Reports why a catalog object cannot be deleted now. Providers are registered during the Core composition:
/// running jobs, image annotations and generation presets.
/// </summary>
internal delegate Task<IReadOnlyList<DeletionBlocker>> DeletionBlockerProvider(string projectId, ProjectStore store, CatalogTargetKind kind,
    Guid targetId, CancellationToken cancellationToken);

internal sealed class CatalogBlockers
{
    private readonly Lock gate = new();
    private DeletionBlockerProvider[] providers = [];

    internal void Register(DeletionBlockerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate) providers = [.. providers, provider];
    }

    internal async Task<IReadOnlyList<DeletionBlocker>> CollectAsync(string projectId, ProjectStore store, CatalogTargetKind kind, Guid targetId,
        CancellationToken cancellationToken)
    {
        DeletionBlockerProvider[] current;
        lock (gate) current = providers;
        var result = new List<DeletionBlocker>();
        foreach (var provider in current)
        {
            result.AddRange(await provider(projectId, store, kind, targetId, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }
}
