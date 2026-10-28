using System.Globalization;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Catalog;

/// <summary>An image prepared by the import job: its file is already in place under files/class-assets.</summary>
internal sealed record ImportedAsset(Guid Id, ClassAssetFormat Format, int Width, int Height, bool HasAlpha, long ByteSize, string Sha256);

/// <summary>Added — rows of this batch, including rows an earlier interrupted run of the same job already inserted.</summary>
internal sealed record ImportBatchOutcome(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Duplicates);

/// <summary>
/// Classes, their bindings to project variants and the source images of the bindings: tables classes, class_variants and
/// class_assets. Bindings reference project variants, so this manager reads the variants table; VariantsManager does not
/// know about classes.
/// </summary>
internal sealed class ClassesManager : IClassesManager
{
    internal const int MaxAssetPage = 500;
    private const string Area = "classes";

    private readonly CatalogContext context;
    private readonly NamedRows<CatalogClass> rows;

    internal ClassesManager(CatalogContext context)
    {
        this.context = context;
        rows = new NamedRows<CatalogClass>(context, Area, "classes", ReadClass,
            item => (item.Id, item.Revision, item.ArchivedAt is not null),
            (item, revisions) => context.Raise(revisions.OrderRevision, revisions.CatalogRevision, classes: [new(item.Id, item.Revision)]));
    }

    public async Task<CatalogWorkspace> GetWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        context.RequireOpen(Area);
        return await context.Store.ReadAsync(async (connection, token) =>
        {
            // One read transaction: the lists and the revisions describe the same moment.
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using var variants = Command(connection, transaction, $"SELECT {VariantColumns} FROM variants ORDER BY sort_order, id;");
            var variantRows = await CatalogSql.ListAsync(variants, ReadVariant, token).ConfigureAwait(false);
            var classRows = await rows.ListAsync(connection, transaction, token).ConfigureAwait(false);
            await using var bindings = Command(connection, transaction,
                $"SELECT {ClassVariantColumns} FROM {ClassVariantFrom} ORDER BY c.sort_order, v.sort_order, b.id;");
            var bindingRows = await CatalogSql.ListAsync(bindings, ReadClassVariant, token).ConfigureAwait(false);
            var state = await ReadStateAsync(connection, transaction, token).ConfigureAwait(false);
            return new CatalogWorkspace(variantRows, classRows, bindingRows, state.OrderRevision, state.CatalogRevision);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CatalogClass> CreateAsync(string name, string description, CancellationToken cancellationToken = default)
        => rows.CreateAsync(name, description, bumpOrder: true, cancellationToken);

    public Task<CatalogClass> UpdateAsync(Guid classId, string name, string description, long expectedRevision,
        CancellationToken cancellationToken = default)
        => rows.UpdateAsync(classId, name, description, expectedRevision, cancellationToken);

    public Task<CatalogClass> SetArchivedAsync(Guid classId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
        => rows.SetArchivedAsync(classId, archived, expectedRevision, cancellationToken);

    public async Task<ClassOrder> ReorderAsync(IReadOnlyList<Guid> classIds, long expectedOrderRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classIds);
        context.RequireOpen(Area);
        var (order, state) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await ReadStateAsync(connection, transaction, token).ConfigureAwait(false);
            if (current.OrderRevision != expectedOrderRevision)
            {
                throw new CoreException("classes.order_revision_conflict",
                    $"Class order has revision {current.OrderRevision}, expected {expectedOrderRevision}.");
            }
            var existing = await rows.ListAsync(connection, transaction, token).ConfigureAwait(false);
            if (classIds.Count != existing.Count || classIds.Distinct().Count() != classIds.Count
                || !existing.Select(item => item.Id).ToHashSet().SetEquals(classIds))
            {
                throw new CoreException("classes.order_invalid", "Class order must contain every class of the project exactly once, including archived ones.");
            }
            var positions = existing.ToDictionary(item => item.Id, item => item.SortOrder);
            await using var command = Command(connection, transaction, "UPDATE classes SET sort_order = $order WHERE id = $id;");
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var position = command.Parameters.Add("$order", SqliteType.Integer);
            for (var index = 0; index < classIds.Count; index++)
            {
                if (positions[classIds[index]] == index) continue;
                id.Value = Key(classIds[index]);
                position.Value = (long)index;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var state = await BumpAsync(connection, transaction, order: true, token).ConfigureAwait(false);
            return (await rows.ListAsync(connection, transaction, token).ConfigureAwait(false), state);
        }, cancellationToken).ConfigureAwait(false);
        context.Raise(state.OrderRevision, state.CatalogRevision);
        return new ClassOrder(order, state.OrderRevision);
    }

    public Task<DeletionReview> ReviewDeletionAsync(Guid classId, CancellationToken cancellationToken = default)
        => context.Deletion.ReviewAsync(Area, CatalogTargetKind.Class, classId, cancellationToken);

    public Task<DeletionResult> DeleteAsync(Guid classId, string reviewRevision, CancellationToken cancellationToken = default)
        => context.Deletion.DeleteAsync(Area, CatalogTargetKind.Class, classId, reviewRevision, cancellationToken);

    public async Task<ClassVariant> GetVariantAsync(Guid classVariantId, CancellationToken cancellationToken = default)
    {
        context.RequireOpen(Area);
        return await context.Store.ReadAsync((connection, token) => FindClassVariantAsync(connection, null, classVariantId, token), cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound(classVariantId);
    }

    public async Task<ClassVariant> AddVariantAsync(Guid classId, Guid variantId, CancellationToken cancellationToken = default)
    {
        context.RequireOpen(Area);
        var (binding, state) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var owner = await rows.FindAsync(connection, transaction, classId, token).ConfigureAwait(false) ?? throw NotFound(classId);
            if (owner.ArchivedAt is not null)
                throw new CoreException("classes.class_unavailable", $"Class {classId} is archived.");
            await using (var exists = Command(connection, transaction, "SELECT count(*) FROM class_variants WHERE class_id = $class AND variant_id = $variant;"))
            {
                exists.Parameters.AddWithValue("$class", Key(classId));
                exists.Parameters.AddWithValue("$variant", Key(variantId));
                if (await ScalarAsync(exists, token).ConfigureAwait(false) > 0)
                    throw new CoreException("classes.duplicate_variant_binding", $"Variant {variantId} is already bound to class {classId}.");
            }
            await RequireActiveVariantAsync(connection, transaction, variantId, token).ConfigureAwait(false);
            var id = Guid.NewGuid();
            var now = Time(Now());
            await using (var insert = Command(connection, transaction, """
                INSERT INTO class_variants (id, class_id, variant_id, color, created_at, updated_at, archived_at, revision)
                VALUES ($id, $class, $variant, NULL, $now, $now, NULL, 1);
                """))
            {
                insert.Parameters.AddWithValue("$id", Key(id));
                insert.Parameters.AddWithValue("$class", Key(classId));
                insert.Parameters.AddWithValue("$variant", Key(variantId));
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var created = (await FindClassVariantAsync(connection, transaction, id, token).ConfigureAwait(false))!;
            return (created, await BumpAsync(connection, transaction, order: false, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        context.Raise(state.OrderRevision, state.CatalogRevision, classVariants: [new(binding.Id, binding.Revision)]);
        return binding;
    }

    public Task<ClassVariant> SetVariantColorAsync(Guid classVariantId, string? color, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var normalized = VariantColors.Normalize(color);
        if (color is not null && normalized is null)
            throw new CoreException("classes.color_invalid", "Variant color must be #rrggbb.");
        return ChangeBindingAsync(classVariantId, expectedRevision, (_, _, current, _) =>
        {
            // Only the binding's own archive makes it read-only; an archived class or project variant leaves its colour editable.
            if (current.ArchivedAt is not null)
                throw new CoreException("classes.variant_archived", $"Class variant {classVariantId} is archived; restore it before editing.");
            return Task.FromResult<BindingUpdate?>(current.Color == normalized
                ? null
                : new BindingUpdate("color = $color", command => command.Parameters.AddWithValue("$color", (object?)normalized ?? DBNull.Value)));
        }, cancellationToken);
    }

    public Task<ClassVariant> SetVariantArchivedAsync(Guid classVariantId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
        => ChangeBindingAsync(classVariantId, expectedRevision, async (connection, transaction, current, token) =>
        {
            if ((current.ArchivedAt is not null) == archived) return null;
            if (!archived)
            {
                var owner = (await rows.FindAsync(connection, transaction, current.ClassId, token).ConfigureAwait(false))!;
                if (owner.ArchivedAt is not null)
                    throw new CoreException("classes.class_unavailable", $"Class {current.ClassId} is archived.");
                await RequireActiveVariantAsync(connection, transaction, current.VariantId, token).ConfigureAwait(false);
            }
            return new BindingUpdate("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value));
        }, cancellationToken);

    public Task<DeletionReview> ReviewVariantDeletionAsync(Guid classVariantId, CancellationToken cancellationToken = default)
        => context.Deletion.ReviewAsync(Area, CatalogTargetKind.ClassVariant, classVariantId, cancellationToken);

    public Task<DeletionResult> DeleteVariantAsync(Guid classVariantId, string reviewRevision, CancellationToken cancellationToken = default)
        => context.Deletion.DeleteAsync(Area, CatalogTargetKind.ClassVariant, classVariantId, reviewRevision, cancellationToken);

    public async Task<ClassAssetPage> ListAssetsAsync(Guid classVariantId, int limit = 100, string? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxAssetPage);
        var cursor = 0L;
        if (after is not null && (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out cursor) || cursor < 0))
            throw new ArgumentException("Asset cursor is invalid.", nameof(after));
        context.RequireOpen(Area);
        return await context.Store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"""
                SELECT {AssetColumns}, seq FROM class_assets
                WHERE class_variant_id = $binding AND seq > $after ORDER BY seq LIMIT $limit;
                """);
            command.Parameters.AddWithValue("$binding", Key(classVariantId));
            command.Parameters.AddWithValue("$after", cursor);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var page = await CatalogSql.ListAsync(command, reader => (Asset: ReadAsset(reader), Seq: reader.GetInt64(10)), token).ConfigureAwait(false);
            if (page.Count == 0 && await FindClassVariantAsync(connection, null, classVariantId, token).ConfigureAwait(false) is null)
                throw NotFound(classVariantId);
            var more = page.Count > limit;
            if (more) page.RemoveAt(page.Count - 1);
            return new ClassAssetPage(page.Select(item => item.Asset).ToArray(),
                more ? page[^1].Seq.ToString(CultureInfo.InvariantCulture) : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DeletionReview> ReviewAssetDeletionAsync(Guid assetId, CancellationToken cancellationToken = default)
        => context.Deletion.ReviewAsync(Area, CatalogTargetKind.Asset, assetId, cancellationToken);

    public Task<DeletionResult> DeleteAssetAsync(Guid assetId, string reviewRevision, CancellationToken cancellationToken = default)
        => context.Deletion.DeleteAsync(Area, CatalogTargetKind.Asset, assetId, reviewRevision, cancellationToken);

    /// <summary>The import job writes only into an available binding; an unavailable or missing one ends the job.</summary>
    internal async Task<ClassVariant> RequireImportTargetAsync(Guid classVariantId, CancellationToken cancellationToken)
    {
        context.RequireOpen(Area);
        var binding = await context.Store.ReadAsync((connection, token) => FindClassVariantAsync(connection, null, classVariantId, token), cancellationToken)
            .ConfigureAwait(false);
        return binding is { Available: true } ? binding : throw VariantUnavailable(classVariantId);
    }

    /// <summary>SHA-256 of the images already in the binding with their IDs, for skipping duplicates before rows are written.</summary>
    internal Task<Dictionary<string, Guid>> ReadHashesAsync(Guid classVariantId, CancellationToken cancellationToken)
        => context.Store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT sha256, id FROM class_assets WHERE class_variant_id = $binding;");
            command.Parameters.AddWithValue("$binding", Key(classVariantId));
            var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result[reader.GetString(0)] = ParseId(reader.GetString(1));
            return result;
        }, cancellationToken);

    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => context.Store.GetFilesAsync(cancellationToken);

    /// <summary>
    /// Inserts one batch of imported images in one transaction. A row with the same ID is a replay of an interrupted run
    /// and counts as added; the same content under another ID is a duplicate, whose file the caller removes.
    /// </summary>
    internal async Task<ImportBatchOutcome> InsertImportedAsync(Guid classVariantId, IReadOnlyList<ImportedAsset> assets, string operationId,
        CancellationToken cancellationToken)
    {
        if (assets.Count == 0) return new ImportBatchOutcome([], []);
        var (outcome, state, inserted) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            if (await FindClassVariantAsync(connection, transaction, classVariantId, token).ConfigureAwait(false) is not { Available: true })
                throw VariantUnavailable(classVariantId);
            var added = new List<Guid>();
            var duplicates = new List<Guid>();
            long count = 0, opaque = 0;
            int width = 0, height = 0;
            await using var insert = Command(connection, transaction, $"""
                INSERT INTO class_assets ({AssetColumns})
                VALUES ($id, $binding, $format, $width, $height, $alpha, $size, $sha, $now, $operation)
                ON CONFLICT DO NOTHING;
                """);
            insert.Parameters.AddWithValue("$binding", Key(classVariantId));
            insert.Parameters.AddWithValue("$now", Time(Now()));
            insert.Parameters.AddWithValue("$operation", operationId);
            var id = insert.Parameters.Add("$id", SqliteType.Text);
            var format = insert.Parameters.Add("$format", SqliteType.Text);
            var w = insert.Parameters.Add("$width", SqliteType.Integer);
            var h = insert.Parameters.Add("$height", SqliteType.Integer);
            var alpha = insert.Parameters.Add("$alpha", SqliteType.Integer);
            var size = insert.Parameters.Add("$size", SqliteType.Integer);
            var sha = insert.Parameters.Add("$sha", SqliteType.Text);
            await using var exists = Command(connection, transaction, "SELECT count(*) FROM class_assets WHERE id = $id;");
            var existing = exists.Parameters.Add("$id", SqliteType.Text);
            foreach (var asset in assets)
            {
                id.Value = Key(asset.Id);
                format.Value = FormatKey(asset.Format);
                w.Value = asset.Width;
                h.Value = asset.Height;
                alpha.Value = asset.HasAlpha ? 1 : 0;
                size.Value = asset.ByteSize;
                sha.Value = asset.Sha256;
                if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1)
                {
                    added.Add(asset.Id);
                    count++;
                    if (!asset.HasAlpha) opaque++;
                    width = Math.Max(width, asset.Width);
                    height = Math.Max(height, asset.Height);
                    continue;
                }
                existing.Value = Key(asset.Id);
                if (await ScalarAsync(exists, token).ConfigureAwait(false) > 0) added.Add(asset.Id);
                else duplicates.Add(asset.Id);
            }
            if (count > 0)
            {
                await using var counters = Command(connection, transaction, """
                    UPDATE class_variants SET asset_count = asset_count + $count, opaque_asset_count = opaque_asset_count + $opaque,
                        max_asset_width = max(max_asset_width, $width), max_asset_height = max(max_asset_height, $height)
                    WHERE id = $binding;
                    """);
                counters.Parameters.AddWithValue("$count", count);
                counters.Parameters.AddWithValue("$opaque", opaque);
                counters.Parameters.AddWithValue("$width", width);
                counters.Parameters.AddWithValue("$height", height);
                counters.Parameters.AddWithValue("$binding", Key(classVariantId));
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var state = count > 0
                ? await BumpAsync(connection, transaction, order: false, token).ConfigureAwait(false)
                : await ReadStateAsync(connection, transaction, token).ConfigureAwait(false);
            return (new ImportBatchOutcome(added, duplicates), state, count);
        }, cancellationToken).ConfigureAwait(false);
        if (inserted > 0)
        {
            context.Raise(state.OrderRevision, state.CatalogRevision, assetsChangedIn: [classVariantId]);
        }
        return outcome;
    }

    /// <summary>Extension of a stored image for the resource channel; null when the row does not exist.</summary>
    internal static async ValueTask<string?> FindAssetExtensionAsync(ProjectStore store, MaterialFiles files, Guid id, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT format FROM class_assets WHERE id = $id;");
            command.Parameters.AddWithValue("$id", Key(id));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string format ? Extension(ParseFormat(format)) : null;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What a dataset plan needs of the catalog, read in one transaction: every class in catalog order with its archive state and
    /// every binding with its project variant name and availability. Not gated on <c>isOpen</c>: a recovered build re-plans while
    /// the project is still being opened.
    /// </summary>
    internal Task<DatasetCatalog> ReadDatasetCatalogAsync(CancellationToken cancellationToken)
        => context.Store.ReadAsync(async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using var classes = Command(connection, transaction, "SELECT id, name, archived_at IS NOT NULL FROM classes ORDER BY sort_order, id;");
            var classRows = await CatalogSql.ListAsync(classes, reader => new DatasetCatalogClass(ParseId(reader.GetString(0)), reader.GetString(1),
                reader.GetInt64(2) == 1), token).ConfigureAwait(false);
            await using var bindings = Command(connection, transaction, """
                SELECT b.id, b.class_id, v.name, (b.archived_at IS NULL AND c.archived_at IS NULL AND v.archived_at IS NULL)
                FROM class_variants b JOIN classes c ON c.id = b.class_id JOIN variants v ON v.id = b.variant_id ORDER BY b.id;
                """);
            var bindingRows = await CatalogSql.ListAsync(bindings, reader => new DatasetCatalogBinding(ParseId(reader.GetString(0)), ParseId(reader.GetString(1)),
                reader.GetString(2), reader.GetInt64(3) == 1), token).ConfigureAwait(false);
            return new DatasetCatalog(classRows, bindingRows);
        }, cancellationToken);

    /// <summary>
    /// What generation needs of the catalog, read in one transaction: the given bindings (missing ones are left out) with their
    /// availability and images in import order, and the given project variants (missing ones are left out). Not gated on
    /// <c>isOpen</c>: a recovered run prepares while the project is still being opened.
    /// </summary>
    internal Task<CatalogSources> ReadGenerationSourcesAsync(IReadOnlyCollection<Guid> bindingIds, IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken)
        => context.Store.ReadAsync(async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using var variants = Command(connection, transaction,
                "SELECT id, archived_at IS NOT NULL FROM variants WHERE id IN (SELECT value FROM json_each($ids));");
            variants.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(variantIds.Select(Key)));
            var variantRows = await CatalogSql.ListAsync(variants, reader => new CatalogSourceVariant(ParseId(reader.GetString(0)), reader.GetInt64(1) == 1),
                token).ConfigureAwait(false);
            await using var bindings = Command(connection, transaction,
                $"SELECT {ClassVariantColumns} FROM {ClassVariantFrom} WHERE b.id IN (SELECT value FROM json_each($ids));");
            bindings.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(bindingIds.Select(Key)));
            var bindingRows = await CatalogSql.ListAsync(bindings, ReadClassVariant, token).ConfigureAwait(false);
            await using var assets = Command(connection, transaction, $"SELECT {AssetColumns} FROM class_assets WHERE class_variant_id = $binding ORDER BY seq;");
            var binding = assets.Parameters.Add("$binding", SqliteType.Text);
            var result = new List<CatalogSourceBinding>(bindingRows.Count);
            foreach (var row in bindingRows)
            {
                binding.Value = Key(row.Id);
                var rows = await CatalogSql.ListAsync(assets, ReadAsset, token).ConfigureAwait(false);
                result.Add(new CatalogSourceBinding(row.Id, row.ClassId, row.VariantId, row.Available,
                    rows.Select(asset => new CatalogSourceAsset(asset.Id, Extension(asset.Format), asset.Width, asset.Height, asset.HasAlpha)).ToArray()));
            }
            return new CatalogSources(result, variantRows);
        }, cancellationToken);

    private sealed record BindingUpdate(string Set, Action<SqliteCommand> Bind);

    private async Task<ClassVariant> ChangeBindingAsync(Guid classVariantId, long expectedRevision,
        Func<SqliteConnection, SqliteTransaction, ClassVariant, CancellationToken, Task<BindingUpdate?>> change, CancellationToken cancellationToken)
    {
        context.RequireOpen(Area);
        var (binding, state) = await context.Store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindClassVariantAsync(connection, transaction, classVariantId, token).ConfigureAwait(false) ?? throw NotFound(classVariantId);
            if (current.Revision != expectedRevision)
                throw new CoreException("classes.revision_conflict", $"Class variant {classVariantId} has revision {current.Revision}, expected {expectedRevision}.");
            if (await change(connection, transaction, current, token).ConfigureAwait(false) is not { } update)
                return (current, ((long, long)?)null);
            await using (var command = Command(connection, transaction,
                $"UPDATE class_variants SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id;"))
            {
                command.Parameters.AddWithValue("$id", Key(classVariantId));
                command.Parameters.AddWithValue("$now", Time(Now()));
                update.Bind(command);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var changed = (await FindClassVariantAsync(connection, transaction, classVariantId, token).ConfigureAwait(false))!;
            return (changed, ((long, long)?)await BumpAsync(connection, transaction, order: false, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (state is { } saved)
        {
            context.Raise(saved.Item1, saved.Item2, classVariants: [new(binding.Id, binding.Revision)]);
        }
        return binding;
    }

    private static async Task RequireActiveVariantAsync(SqliteConnection connection, SqliteTransaction transaction, Guid variantId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT count(*) FROM variants WHERE id = $id AND archived_at IS NULL;");
        command.Parameters.AddWithValue("$id", Key(variantId));
        if (await ScalarAsync(command, cancellationToken).ConfigureAwait(false) == 0)
            throw new CoreException("classes.project_variant_unavailable", $"Project variant {variantId} is missing or archived.");
    }

    private static CoreException NotFound(Guid id) => new("classes.not_found", $"Catalog object {id} was not found in classes.");

    private static CoreException VariantUnavailable(Guid id) => new("classes.variant_unavailable", $"Class variant {id} is missing or unavailable.");
}
