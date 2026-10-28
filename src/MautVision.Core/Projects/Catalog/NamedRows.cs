using MautVision.Core.Interfaces;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Catalog;

/// <summary>
/// Create, rename and archive for the two named lists of the catalog, variants and classes: their tables have the same
/// columns and rules (unique case-insensitive names including the archive, revision on every change, new rows at the end).
/// </summary>
internal sealed class NamedRows<T>(
    CatalogContext context,
    string area,
    string table,
    Func<SqliteDataReader, T> read,
    Func<T, (Guid Id, long Revision, bool Archived)> state,
    Action<T, (long OrderRevision, long CatalogRevision)> raise) where T : class
{
    // Every named table has the same column list; see CatalogSql.VariantColumns and ClassColumns.
    private const string Columns = VariantColumns;

    internal async Task<IReadOnlyList<T>> ListAsync(CancellationToken cancellationToken)
    {
        context.RequireOpen(area);
        return await context.Store.ReadAsync(async (connection, token) => (IReadOnlyList<T>)await ListAsync(connection, null, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<List<T>> ListAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns} FROM {table} ORDER BY sort_order, id;");
        return await CatalogSql.ListAsync(command, read, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>bumpOrder: the new row changes the order that reorder requests are checked against.</summary>
    internal async Task<T> CreateAsync(string name, string description, bool bumpOrder, CancellationToken cancellationToken)
    {
        var text = ValidateText(area, name, description);
        context.RequireOpen(area);
        var (created, revisions) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            await RequireUniqueAsync(connection, transaction, text.Key, null, token).ConfigureAwait(false);
            await using var command = Command(connection, transaction, $"""
                INSERT INTO {table} ({Columns}, name_key)
                VALUES ($id, $name, $description, (SELECT coalesce(max(sort_order), -1) + 1 FROM {table}), $now, $now, NULL, 1, $key)
                RETURNING {Columns};
                """);
            command.Parameters.AddWithValue("$id", Key(Guid.NewGuid()));
            command.Parameters.AddWithValue("$name", text.Name);
            command.Parameters.AddWithValue("$description", text.Description);
            command.Parameters.AddWithValue("$now", Time(Now()));
            command.Parameters.AddWithValue("$key", text.Key);
            var row = (await SingleOrDefaultAsync(command, read, token).ConfigureAwait(false))!;
            return (row, await BumpAsync(connection, transaction, bumpOrder, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        raise(created, revisions);
        return created;
    }

    internal Task<T> UpdateAsync(Guid id, string name, string description, long expectedRevision, CancellationToken cancellationToken)
    {
        var text = ValidateText(area, name, description);
        return ChangeAsync(id, expectedRevision, async (connection, transaction, current, token) =>
        {
            // An archived row is read-only until it is restored; archiving and restoring stay allowed.
            if (state(current).Archived)
                throw new CoreException($"{area}.archived", $"{table} row {id} is archived; restore it before editing.");
            await RequireUniqueAsync(connection, transaction, text.Key, id, token).ConfigureAwait(false);
            return new Update("name = $name, name_key = $key, description = $description", command =>
            {
                command.Parameters.AddWithValue("$name", text.Name);
                command.Parameters.AddWithValue("$key", text.Key);
                command.Parameters.AddWithValue("$description", text.Description);
            });
        }, cancellationToken);
    }

    internal Task<T> SetArchivedAsync(Guid id, bool archived, long expectedRevision, CancellationToken cancellationToken)
        => ChangeAsync(id, expectedRevision, (_, _, current, _) => Task.FromResult<Update?>(state(current).Archived == archived
            ? null
            : new Update("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value))),
            cancellationToken);

    internal async Task<T?> FindAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {Columns} FROM {table} WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, read, cancellationToken).ConfigureAwait(false);
    }

    internal CoreException NotFound(Guid id) => new($"{area}.not_found", $"{table} row {id} was not found.");

    private sealed record Update(string Set, Action<SqliteCommand> Bind);

    /// <summary>Checks the revision and applies one UPDATE; a change that returns null saves nothing and keeps the revision.</summary>
    private async Task<T> ChangeAsync(Guid id, long expectedRevision,
        Func<SqliteConnection, SqliteTransaction, T, CancellationToken, Task<Update?>> change, CancellationToken cancellationToken)
    {
        context.RequireOpen(area);
        var (row, revisions) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindAsync(connection, transaction, id, token).ConfigureAwait(false) ?? throw NotFound(id);
            if (state(current).Revision != expectedRevision)
                throw new CoreException($"{area}.revision_conflict", $"{table} row {id} has revision {state(current).Revision}, expected {expectedRevision}.");
            if (await change(connection, transaction, current, token).ConfigureAwait(false) is not { } update)
                return (current, ((long, long)?)null);
            await using var command = Command(connection, transaction,
                $"UPDATE {table} SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id RETURNING {Columns};");
            command.Parameters.AddWithValue("$id", Key(id));
            command.Parameters.AddWithValue("$now", Time(Now()));
            update.Bind(command);
            var changed = (await SingleOrDefaultAsync(command, read, token).ConfigureAwait(false))!;
            return (changed, ((long, long)?)await BumpAsync(connection, transaction, order: false, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (revisions is { } saved)
        {
            raise(row, saved);
        }
        return row;
    }

    private async Task RequireUniqueAsync(SqliteConnection connection, SqliteTransaction transaction, string key, Guid? except,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT count(*) FROM {table} WHERE name_key = $key AND id != $except;");
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$except", except is { } value ? Key(value) : "");
        if (await ScalarAsync(command, cancellationToken).ConfigureAwait(false) > 0)
            throw new CoreException($"{area}.name_duplicate", $"A {table} row with this name already exists, including the archive.");
    }
}
