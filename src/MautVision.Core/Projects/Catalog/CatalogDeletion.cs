using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Catalog;

/// <summary>
/// Permanent deletion of catalog objects. The review describes the target and every row deleted with it by a digest;
/// deletion recomputes it in the write transaction and refuses a mismatch. Deletion is two-phase: the rows go and their
/// files are queued in one transaction, then <see cref="FileCleanup"/> removes the files in the background.
/// </summary>
internal sealed class CatalogDeletion(CatalogContext context, CatalogBlockers blockers)
{
    internal const string AssetKind = ClassAsset.ResourceKind;

    internal async Task<DeletionReview> ReviewAsync(string area, CatalogTargetKind kind, Guid targetId, CancellationToken cancellationToken)
    {
        context.RequireOpen(area);
        var composition = await context.Store.ReadAsync((connection, token) => ComposeAsync(connection, null, area, kind, targetId, token),
            cancellationToken).ConfigureAwait(false);
        var found = await blockers.CollectAsync(context.ProjectId, context.Store, kind, targetId, cancellationToken).ConfigureAwait(false);
        return new DeletionReview(kind, targetId, composition.Name, composition.Classes, composition.ClassVariants, composition.Assets,
            found, composition.Revision);
    }

    internal async Task<DeletionResult> DeleteAsync(string area, CatalogTargetKind kind, Guid targetId, string reviewRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRevision);
        context.RequireOpen(area);
        var found = await blockers.CollectAsync(context.ProjectId, context.Store, kind, targetId, cancellationToken).ConfigureAwait(false);
        if (found.Count > 0)
        {
            throw new CoreException($"{area}.deletion_blocked",
                $"Deletion of {kind} {targetId} is blocked: {string.Join(", ", found.Select(blocker => blocker.Code))}.");
        }
        var (composition, files, state) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var composition = await ComposeAsync(connection, transaction, area, kind, targetId, token).ConfigureAwait(false);
            if (composition.Revision != reviewRevision)
            {
                throw new CoreException($"{area}.deletion_revision_conflict", $"Deletion review of {kind} {targetId} is outdated.");
            }
            var files = await ReadFilesAsync(connection, transaction, kind, targetId, token).ConfigureAwait(false);
            await FileCleanup.QueueAsync(connection, transaction, files, token).ConfigureAwait(false);
            await DeleteRowsAsync(connection, transaction, kind, targetId, composition, token).ConfigureAwait(false);
            var state = await BumpAsync(connection, transaction, order: kind == CatalogTargetKind.Class, token).ConfigureAwait(false);
            return (composition, files, state);
        }, cancellationToken).ConfigureAwait(false);

        var removed = composition.Bindings.Select(id => new CatalogEntityChange(id, null)).ToArray();
        switch (kind)
        {
            case CatalogTargetKind.Variant:
                context.Raise(state.OrderRevision, state.CatalogRevision, variants: [new(targetId, null)], classVariants: removed);
                break;
            case CatalogTargetKind.Class:
                context.Raise(state.OrderRevision, state.CatalogRevision, classes: [new(targetId, null)], classVariants: removed);
                break;
            case CatalogTargetKind.ClassVariant:
                context.Raise(state.OrderRevision, state.CatalogRevision, classVariants: removed);
                break;
            default:
                context.Raise(state.OrderRevision, state.CatalogRevision, assetsChangedIn: composition.Bindings);
                break;
        }
        context.Logger.LogInformation("Project {ProjectId} {Kind} {TargetId} deleted with {ClassVariants} class variants and {Assets} assets.",
            context.ProjectId, kind, targetId, composition.ClassVariants, composition.Assets);

        // Files go in the background; the deletion already succeeded, so cleanup problems only leave files queued.
        var pending = await context.Cleanup.RemoveAfterCommitAsync(files).ConfigureAwait(false);
        return new DeletionResult(composition.Classes, composition.ClassVariants, composition.Assets, pending);
    }

    private sealed record Composition(string Name, long Classes, long ClassVariants, long Assets, IReadOnlyList<Guid> Bindings, string Revision);

    /// <summary>Counts and the digest of the target and every row deleted with it.</summary>
    private static async Task<Composition> ComposeAsync(SqliteConnection connection, SqliteTransaction? transaction, string area,
        CatalogTargetKind kind, Guid targetId, CancellationToken cancellationToken)
    {
        var digest = new StringBuilder().Append(kind).Append('|').Append(Key(targetId)).Append('|');
        string name;
        string bindingFilter;
        switch (kind)
        {
            case CatalogTargetKind.Variant:
            case CatalogTargetKind.Class:
            {
                var table = kind == CatalogTargetKind.Variant ? "variants" : "classes";
                await using var target = Command(connection, transaction, $"SELECT name, revision FROM {table} WHERE id = $id;");
                target.Parameters.AddWithValue("$id", Key(targetId));
                var row = await SingleOrDefaultAsync(target, reader => Tuple.Create(reader.GetString(0), reader.GetInt64(1)), cancellationToken)
                    .ConfigureAwait(false) ?? throw NotFound(area, kind, targetId);
                name = row.Item1;
                digest.Append(row.Item2);
                bindingFilter = kind == CatalogTargetKind.Variant ? "variant_id = $id" : "class_id = $id";
                break;
            }
            case CatalogTargetKind.ClassVariant:
            {
                await using var target = Command(connection, transaction,
                    "SELECT v.name FROM class_variants b JOIN variants v ON v.id = b.variant_id WHERE b.id = $id;");
                target.Parameters.AddWithValue("$id", Key(targetId));
                name = await SingleOrDefaultAsync(target, reader => reader.GetString(0), cancellationToken).ConfigureAwait(false)
                    ?? throw NotFound(area, kind, targetId);
                bindingFilter = "id = $id";
                break;
            }
            case CatalogTargetKind.Asset:
            {
                await using var target = Command(connection, transaction, "SELECT seq, class_variant_id FROM class_assets WHERE id = $id;");
                target.Parameters.AddWithValue("$id", Key(targetId));
                var row = await SingleOrDefaultAsync(target, reader => Tuple.Create(reader.GetInt64(0), reader.GetString(1)), cancellationToken)
                    .ConfigureAwait(false) ?? throw NotFound(area, kind, targetId);
                digest.Append(row.Item1);
                return new Composition("", 0, 0, 1, [ParseId(row.Item2)], Digest(digest));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown catalog target.");
        }

        await using var bindings = Command(connection, transaction, $"SELECT id, class_id, revision FROM class_variants WHERE {bindingFilter} ORDER BY id;");
        bindings.Parameters.AddWithValue("$id", Key(targetId));
        var rows = await ListAsync(bindings, reader => (Id: reader.GetString(0), ClassId: reader.GetString(1), Revision: reader.GetInt64(2)), cancellationToken)
            .ConfigureAwait(false);
        foreach (var row in rows) digest.Append('|').Append(row.Id).Append(':').Append(row.Revision);

        await using var assets = Command(connection, transaction,
            $"SELECT count(*), coalesce(max(seq), 0) FROM class_assets WHERE class_variant_id IN (SELECT id FROM class_variants WHERE {bindingFilter});");
        assets.Parameters.AddWithValue("$id", Key(targetId));
        var (count, last) = await SingleOrDefaultAsync(assets, reader => Tuple.Create(reader.GetInt64(0), reader.GetInt64(1)), cancellationToken)
            .ConfigureAwait(false) is { } stats ? (stats.Item1, stats.Item2) : (0L, 0L);
        digest.Append('|').Append(count).Append(':').Append(last);

        var classes = kind switch
        {
            CatalogTargetKind.Variant => rows.Select(row => row.ClassId).Distinct(StringComparer.Ordinal).LongCount(),
            CatalogTargetKind.Class => 1,
            _ => 0,
        };
        return new Composition(name, classes, rows.Count, count, rows.Select(row => ParseId(row.Id)).ToArray(), Digest(digest));
    }

    private static string Digest(StringBuilder value)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())).AsSpan(0, 16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<IReadOnlyList<QueuedFile>> ReadFilesAsync(SqliteConnection connection, SqliteTransaction transaction,
        CatalogTargetKind kind, Guid targetId, CancellationToken cancellationToken)
    {
        var filter = kind switch
        {
            CatalogTargetKind.Variant => "class_variant_id IN (SELECT id FROM class_variants WHERE variant_id = $id)",
            CatalogTargetKind.Class => "class_variant_id IN (SELECT id FROM class_variants WHERE class_id = $id)",
            CatalogTargetKind.ClassVariant => "class_variant_id = $id",
            _ => "id = $id",
        };
        await using var command = Command(connection, transaction, $"SELECT id, format FROM class_assets WHERE {filter};");
        command.Parameters.AddWithValue("$id", Key(targetId));
        return await ListAsync(command, reader => new QueuedFile(AssetKind, ParseId(reader.GetString(0)), Extension(ParseFormat(reader.GetString(1)))), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task DeleteRowsAsync(SqliteConnection connection, SqliteTransaction transaction, CatalogTargetKind kind, Guid targetId,
        Composition composition, CancellationToken cancellationToken)
    {
        var table = kind switch
        {
            CatalogTargetKind.Variant => "variants",
            CatalogTargetKind.Class => "classes",
            CatalogTargetKind.ClassVariant => "class_variants",
            _ => "class_assets",
        };
        // Bindings and assets go with their owner through ON DELETE CASCADE.
        await using (var command = Command(connection, transaction, $"DELETE FROM {table} WHERE id = $id;"))
        {
            command.Parameters.AddWithValue("$id", Key(targetId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (kind == CatalogTargetKind.Asset)
        {
            await RecountAsync(connection, transaction, composition.Bindings[0], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Recomputes the stored image counters of a binding after images were removed.</summary>
    internal static async Task RecountAsync(SqliteConnection connection, SqliteTransaction transaction, Guid classVariantId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            UPDATE class_variants SET
                asset_count = (SELECT count(*) FROM class_assets WHERE class_variant_id = $id),
                opaque_asset_count = (SELECT count(*) FROM class_assets WHERE class_variant_id = $id AND has_alpha = 0),
                max_asset_width = (SELECT coalesce(max(width), 0) FROM class_assets WHERE class_variant_id = $id),
                max_asset_height = (SELECT coalesce(max(height), 0) FROM class_assets WHERE class_variant_id = $id)
            WHERE id = $id;
            """);
        command.Parameters.AddWithValue("$id", Key(classVariantId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CoreException NotFound(string area, CatalogTargetKind kind, Guid targetId)
        => new($"{area}.not_found", $"Catalog {kind} {targetId} was not found.");
}
