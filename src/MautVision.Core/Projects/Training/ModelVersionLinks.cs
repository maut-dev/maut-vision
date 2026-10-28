using MautVision.Core.Interfaces.Projects.Catalog;
using Microsoft.Data.Sqlite;

namespace MautVision.Core.Projects.Training;

/// <summary>Reports why a model version cannot be deleted now (an evaluation, a live check or an export of it).</summary>
internal delegate Task<IReadOnlyList<DeletionBlocker>> ModelVersionBlockerProvider(string projectId, Guid versionId, CancellationToken cancellationToken);

/// <summary>
/// Rows of another area that belong to a version and go with it (its evaluations): Count for the deletion review, Delete inside the
/// deletion transaction; both return the number of dependent objects.
/// </summary>
internal sealed record ModelVersionDependents(
    Func<SqliteConnection, SqliteTransaction?, Guid, CancellationToken, Task<int>> CountAsync,
    Func<SqliteConnection, SqliteTransaction, Guid, CancellationToken, Task<int>> DeleteAsync);

/// <summary>
/// What other areas hold on a model version. Training does not know about checks: the Core composition registers their blockers
/// and the rows deleted with a version, as it does for dataset versions.
/// </summary>
internal sealed class ModelVersionLinks
{
    private readonly Lock gate = new();
    private ModelVersionBlockerProvider[] blockers = [];
    private ModelVersionDependents[] dependents = [];

    internal void RegisterBlocker(ModelVersionBlockerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (gate) blockers = [.. blockers, provider];
    }

    internal void RegisterDependents(ModelVersionDependents value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate) dependents = [.. dependents, value];
    }

    internal async Task<IReadOnlyList<DeletionBlocker>> CollectBlockersAsync(string projectId, Guid versionId, CancellationToken cancellationToken)
    {
        ModelVersionBlockerProvider[] current;
        lock (gate) current = blockers;
        var result = new List<DeletionBlocker>();
        foreach (var provider in current) result.AddRange(await provider(projectId, versionId, cancellationToken).ConfigureAwait(false));
        return result;
    }

    internal async Task<int> CountDependentsAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid versionId, CancellationToken cancellationToken)
    {
        ModelVersionDependents[] current;
        lock (gate) current = dependents;
        var total = 0;
        foreach (var item in current) total += await item.CountAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);
        return total;
    }

    internal async Task<int> DeleteDependentsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid versionId, CancellationToken cancellationToken)
    {
        ModelVersionDependents[] current;
        lock (gate) current = dependents;
        var total = 0;
        foreach (var item in current) total += await item.DeleteAsync(connection, transaction, versionId, cancellationToken).ConfigureAwait(false);
        return total;
    }
}
