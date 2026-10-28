using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Datasets;

/// <summary>
/// Reports why a dataset version cannot be deleted now. Providers are registered during the Core composition: a check or an
/// export of this version; training and model checks on it register theirs. Operations of other versions do not
/// block.
/// </summary>
internal delegate Task<IReadOnlyList<DeletionBlocker>> DatasetBlockerProvider(string projectId, ProjectStore store, Guid versionId,
    CancellationToken cancellationToken);

internal sealed class DatasetBlockers
{
    /// <summary>A queued or running integrity check of the version.</summary>
    internal const string VerifyActive = "datasets.verify_active";

    /// <summary>A queued or running ZIP export of the version.</summary>
    internal const string ExportActive = "datasets.export_active";

    /// <summary>The version is being materialized into a job folder (training reads it).</summary>
    internal const string ReaderActive = "datasets.reader_active";

    private readonly Lock gate = new();
    private DatasetBlockerProvider[] providers = [];

    internal void Register(DatasetBlockerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate) providers = [.. providers, provider];
    }

    internal async Task<IReadOnlyList<DeletionBlocker>> CollectAsync(string projectId, ProjectStore store, Guid versionId, CancellationToken cancellationToken)
    {
        DatasetBlockerProvider[] current;
        lock (gate) current = providers;
        var result = new List<DeletionBlocker>();
        foreach (var provider in current)
        {
            result.AddRange(await provider(projectId, store, versionId, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }
}
