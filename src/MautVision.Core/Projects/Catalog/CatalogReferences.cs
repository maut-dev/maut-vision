using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Catalog;

/// <summary>
/// Checks references of other materials to the catalog (a class and optionally a class variant of that class) inside the
/// caller's transaction, so that a concurrent archive cannot slip between the check and the write. The catalog owns this
/// SQL; image annotations call it.
/// </summary>
internal static class CatalogReferences
{
    /// <summary>
    /// True when the class exists and the class variant, if given, belongs to it. With <paramref name="requireActive"/> the
    /// class must not be archived and the class variant must be available (it, its class and its project variant active).
    /// </summary>
    internal static async Task<bool> IsValidAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid classId, Guid? classVariantId,
        bool requireActive, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, classVariantId is null
            ? "SELECT archived_at IS NULL FROM classes WHERE id = $class;"
            : $"SELECT (b.archived_at IS NULL AND c.archived_at IS NULL AND v.archived_at IS NULL) FROM {ClassVariantFrom} WHERE b.id = $binding AND b.class_id = $class;");
        command.Parameters.AddWithValue("$class", Key(classId));
        if (classVariantId is { } binding) command.Parameters.AddWithValue("$binding", Key(binding));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull) return false;
        return !requireActive || Convert.ToInt64(value) == 1;
    }
}
