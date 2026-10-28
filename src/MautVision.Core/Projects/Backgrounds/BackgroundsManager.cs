using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using MautVision.Core.Jobs;
using MautVision.Core.Media;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Backgrounds.BackgroundsSql;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>A background prepared by the run: its PNG is already in place under files/backgrounds.</summary>
internal sealed record ImportedBackground(Guid Id, int Width, int Height, long ByteSize, string Sha256, string PixelHash, byte[] Signature,
    BackgroundOrigin Origin);

/// <summary>A set as generation reads it: size, archive, content revision and its active backgrounds (files/backgrounds/&lt;id&gt;.png) in order.</summary>
internal sealed record BackgroundSourceSet(Guid Id, string Name, InputSize TargetSize, bool Archived, long ContentRevision, IReadOnlyList<Guid> Items);

/// <summary>Added — rows of this batch (also rows an interrupted run of the same job already inserted); Duplicates — exact repeats.</summary>
internal sealed record BackgroundBatchOutcome(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Duplicates);

/// <summary>
/// Background sets and their items of one project: tables background_sets and background_items. Previews for the add dialog
/// (headers, scaled source frames, samples of a run) are computed here without storing anything.
/// </summary>
internal sealed class BackgroundsManager : IBackgroundsManager
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 4000;
    internal const int MaxPage = 200;
    internal const int MaxActivePage = 1000;
    internal const int MaxSelection = 400;
    private const string FileKind = BackgroundItem.ResourceKind;
    private const string FileExtension = ".png";

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Func<InputSize> projectInput;
    private readonly Action<BackgroundsChangedEventArgs> changed;
    private readonly FileCleanup cleanup;
    private readonly BackgroundBlockers blockers;
    private readonly BackgroundPreviews previews;
    private readonly ILogger logger;

    internal BackgroundsManager(string projectId, ProjectStore store, Func<bool> isOpen, Func<InputSize> projectInput,
        Action<BackgroundsChangedEventArgs> changed, FileCleanup cleanup, BackgroundBlockers blockers, MediaManager media, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.projectInput = projectInput;
        this.changed = changed;
        this.cleanup = cleanup;
        this.blockers = blockers;
        this.logger = logger;
        previews = new BackgroundPreviews(media);
    }

    public async Task<IReadOnlyList<BackgroundSet>> ListSetsAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        var input = projectInput();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {SetColumns} FROM background_sets ORDER BY created_at, id;");
            return (IReadOnlyList<BackgroundSet>)await ListAsync(command, reader => ReadSet(reader, input), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundSet> GetSetAsync(Guid setId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindSetAsync(connection, null, setId, projectInput(), token), cancellationToken)
            .ConfigureAwait(false) ?? throw SetNotFound(setId);
    }

    public async Task<BackgroundSet> CreateSetAsync(string name, string description, CancellationToken cancellationToken = default)
    {
        var text = ValidateText(name, description);
        RequireOpen();
        var input = projectInput();
        var set = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, $"""
                INSERT INTO background_sets (id, name, description, target_width, target_height, created_at, updated_at, revision)
                VALUES ($id, $name, $description, $width, $height, $now, $now, 1)
                RETURNING {SetColumns};
                """);
            command.Parameters.AddWithValue("$id", Key(Guid.NewGuid()));
            command.Parameters.AddWithValue("$name", text.Name);
            command.Parameters.AddWithValue("$description", text.Description);
            command.Parameters.AddWithValue("$width", input.Width);
            command.Parameters.AddWithValue("$height", input.Height);
            command.Parameters.AddWithValue("$now", Time(Now()));
            return (await SingleOrDefaultAsync(command, reader => ReadSet(reader, input), token).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);
        Raise(sets: [new(set.Id, set.Revision)]);
        return set;
    }

    public Task<BackgroundSet> UpdateSetAsync(Guid setId, string name, string description, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var text = ValidateText(name, description);
        return ChangeSetAsync(setId, expectedRevision, current =>
        {
            if (current.ArchivedAt is not null) throw SetArchived(setId);
            if (current.Name == text.Name && current.Description == text.Description) return null;
            return new SetUpdate("name = $name, description = $description", command =>
            {
                command.Parameters.AddWithValue("$name", text.Name);
                command.Parameters.AddWithValue("$description", text.Description);
            });
        }, cancellationToken);
    }

    public async Task<BackgroundSet> SetSetArchivedAsync(Guid setId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        if (archived)
        {
            // Checked before the write: a run that starts right after fails at its next batch on the archived set.
            var found = await blockers.CollectAsync(projectId, store, setId, null, cancellationToken).ConfigureAwait(false);
            if (found.Any(blocker => blocker.Code == BackgroundBlockers.ImportActive))
                throw new CoreException("backgrounds.set_importing", $"Background set {setId} is being filled by a run.");
        }
        return await ChangeSetAsync(setId, expectedRevision, current => (current.ArchivedAt is not null) == archived
            ? null
            : new SetUpdate("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundPage> ListItemsAsync(Guid setId, BackgroundFilter filter, int limit = 100, string? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        var scope = filter.Removed switch { null => "*", true => "r", false => "a" };
        var cursor = after is null ? 0L : ParseCursor(after, scope);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var (page, next) = await ReadPageAsync(connection, null, setId, filter.Removed, limit, cursor, scope, token).ConfigureAwait(false);
            if (page.Count == 0 && await FindSetAsync(connection, null, setId, projectInput(), token).ConfigureAwait(false) is null) throw SetNotFound(setId);
            return new BackgroundPage(page, next);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BackgroundItem> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindItemAsync(connection, null, itemId, token), cancellationToken).ConfigureAwait(false)
            ?? throw ItemNotFound(itemId);
    }

    public async Task<IReadOnlyList<BackgroundItem>> SetItemsRemovedAsync(IReadOnlyList<BackgroundRevision> items, bool removed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidateIds(items.Select(item => item?.Id ?? Guid.Empty).ToArray());
        RequireOpen();
        var input = projectInput();
        var (updated, sets) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var checkedSets = new HashSet<Guid>();
            var result = new List<BackgroundItem>(items.Count);
            var delta = new Dictionary<Guid, int>();
            await using var update = Command(connection, transaction, "UPDATE background_items SET removed_at = $removed, revision = revision + 1 WHERE id = $id;");
            var id = update.Parameters.Add("$id", SqliteType.Text);
            update.Parameters.AddWithValue("$removed", removed ? Time(Now()) : DBNull.Value);
            foreach (var item in items)
            {
                var current = await FindItemAsync(connection, transaction, item.Id, token).ConfigureAwait(false) ?? throw ItemNotFound(item.Id);
                if (checkedSets.Add(current.SetId))
                {
                    var set = (await FindSetAsync(connection, transaction, current.SetId, input, token).ConfigureAwait(false))!;
                    if (set.ArchivedAt is not null) throw SetArchived(set.Id);
                }
                if (current.Revision != item.Revision)
                    throw RevisionConflict($"Background {item.Id} has revision {current.Revision}, expected {item.Revision}.");
                if ((current.RemovedAt is not null) == removed)
                {
                    result.Add(current);
                    continue;
                }
                id.Value = Key(current.Id);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                delta[current.SetId] = delta.GetValueOrDefault(current.SetId) + 1;
                result.Add((await FindItemAsync(connection, transaction, current.Id, token).ConfigureAwait(false))!);
            }
            await using var counters = Command(connection, transaction, """
                UPDATE background_sets SET item_count = item_count - $delta, removed_count = removed_count + $delta,
                    content_revision = content_revision + 1, updated_at = $now WHERE id = $id;
                """);
            counters.Parameters.AddWithValue("$now", Time(Now()));
            var setId = counters.Parameters.Add("$id", SqliteType.Text);
            var change = counters.Parameters.Add("$delta", SqliteType.Integer);
            foreach (var (owner, count) in delta)
            {
                setId.Value = Key(owner);
                change.Value = removed ? count : -count;
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return (result, delta.Keys.ToArray());
        }, cancellationToken).ConfigureAwait(false);
        if (sets.Length > 0) Raise(items: updated.Select(item => new BackgroundsEntityChange(item.Id, item.Revision)).ToArray(), changedIn: sets);
        return updated;
    }

    public async Task<BackgroundActivePage> ListActiveItemsAsync(Guid setId, int limit = 500, string? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxActivePage);
        const string Scope = "g";
        var cursor = after is null ? 0L : ParseCursor(after, Scope);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            // One read transaction: the revision and the page come from the same snapshot.
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            var set = await FindSetAsync(connection, transaction, setId, projectInput(), token).ConfigureAwait(false) ?? throw SetNotFound(setId);
            var (page, next) = await ReadPageAsync(connection, transaction, setId, false, limit, cursor, Scope, token).ConfigureAwait(false);
            return new BackgroundActivePage(setId, set.TargetSize, set.ContentRevision, page, next);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<JobInfo>> ListRunsAsync(Guid setId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            if (await FindSetAsync(connection, null, setId, projectInput(), token).ConfigureAwait(false) is null) throw SetNotFound(setId);
            return await JobsTable.ListByTargetAsync(connection, projectId, ImportBackgroundsHandler.Kind, setId, limit, before, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<BackgroundDeletionReview> ReviewSetDeletionAsync(Guid setId, CancellationToken cancellationToken = default)
        => ReviewDeletionAsync(setId, null, cancellationToken);

    public Task<BackgroundDeletionResult> DeleteSetAsync(Guid setId, string reviewRevision, CancellationToken cancellationToken = default)
        => DeleteAsync(setId, null, reviewRevision, cancellationToken);

    public Task<BackgroundDeletionReview> ReviewItemsDeletionAsync(Guid setId, IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(itemIds);
        return ReviewDeletionAsync(setId, itemIds, cancellationToken);
    }

    public Task<BackgroundDeletionResult> DeleteItemsAsync(Guid setId, IReadOnlyList<Guid> itemIds, string reviewRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(itemIds);
        return DeleteAsync(setId, itemIds, reviewRevision, cancellationToken);
    }

    public async Task<IReadOnlyList<BackgroundSourceInfo>> InspectFilesAsync(Guid setId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var set = await GetSetAsync(setId, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => BackgroundPreviews.Inspect(paths, set.TargetSize, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<BackgroundSourcePreview> PreviewFileAsync(string path, int maxSide = 1600, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return previews.PreviewFileAsync(path, maxSide, cancellationToken);
    }

    public Task<BackgroundSourcePreview> PreviewVideoFrameAsync(string path, long timestampMs, int maxSide = 1600, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return previews.PreviewVideoFrameAsync(path, timestampMs, maxSide, cancellationToken);
    }

    public async Task<BackgroundImportPreview> PreviewImportAsync(ImportBackgroundsInput input, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        input.Validate();
        RequireOpen();
        var set = await RequireImportTargetAsync(input.SetId, cancellationToken).ConfigureAwait(false);
        var blur = BlurPlan.Resolve(projectId, operationId, input.Blur);
        var similar = input.SkipSimilar ? await LoadSignaturesAsync(set.Id, cancellationToken).ConfigureAwait(false) : null;
        var samples = await previews.SamplesAsync(input, set.TargetSize, blur, similar,
            (hashes, token) => FindPixelHashesAsync(set.Id, hashes, [], token), cancellationToken).ConfigureAwait(false);
        return new BackgroundImportPreview(set.TargetSize, blur, samples);
    }

    // ---- Run support ----

    /// <summary>
    /// A run writes only into an existing active set; anything else ends it. Not gated on <c>isOpen</c>: recovered jobs are
    /// dispatched while the project is still being opened, after its store is ready.
    /// </summary>
    internal async Task<BackgroundSet> RequireImportTargetAsync(Guid setId, CancellationToken cancellationToken)
    {
        var set = await store.ReadAsync((connection, token) => FindSetAsync(connection, null, setId, projectInput(), token), cancellationToken)
            .ConfigureAwait(false) ?? throw SetNotFound(setId);
        if (set.ArchivedAt is not null) throw SetArchived(setId);
        return set;
    }

    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => store.GetFilesAsync(cancellationToken);

    /// <summary>Signatures of every background of the set, removed ones included, in one indexed scan.</summary>
    internal Task<SimilarityIndex> LoadSignaturesAsync(Guid setId, CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            await using var count = Command(connection, null, "SELECT item_count + removed_count FROM background_sets WHERE id = $set;");
            count.Parameters.AddWithValue("$set", Key(setId));
            var index = new SimilarityIndex((int)Math.Min(1 << 20, Convert.ToInt64(await count.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L) + 64));
            await using var command = Command(connection, null, "SELECT signature FROM background_items WHERE set_id = $set ORDER BY ordinal;");
            command.Parameters.AddWithValue("$set", Key(setId));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var buffer = new byte[BackgroundSignature.Length];
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (reader.GetBytes(0, 0, buffer, 0, buffer.Length) == buffer.Length) index.Add(buffer);
            }
            return index;
        }, cancellationToken);

    /// <summary>Pixel hashes of this list that backgrounds of the set (not the given IDs) already have; indexed lookups.</summary>
    internal Task<HashSet<string>> FindPixelHashesAsync(Guid setId, IReadOnlyCollection<string> hashes, IReadOnlyCollection<Guid> exceptIds,
        CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (hashes.Count == 0) return result;
            await using var command = Command(connection, null, """
                SELECT DISTINCT pixel_hash FROM background_items WHERE set_id = $set AND pixel_hash IN (SELECT value FROM json_each($hashes))
                    AND id NOT IN (SELECT value FROM json_each($except));
                """);
            command.Parameters.AddWithValue("$set", Key(setId));
            command.Parameters.AddWithValue("$hashes", JsonSerializer.Serialize(hashes));
            command.Parameters.AddWithValue("$except", JsonIds(exceptIds));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(reader.GetString(0));
            return result;
        }, cancellationToken);

    /// <summary>
    /// Inserts one batch in one transaction at the end of the set. A row with the same ID is a replay of an interrupted run
    /// and counts as added; the same pixels already in the set (committed meanwhile by another run) make a duplicate, whose
    /// file the caller removes. raise: false lets the caller throttle the change event.
    /// </summary>
    internal async Task<BackgroundBatchOutcome> InsertImportedAsync(Guid setId, IReadOnlyList<ImportedBackground> items, bool raise,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return new BackgroundBatchOutcome([], []);
        var input = projectInput();
        var (outcome, inserted) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var set = await FindSetAsync(connection, transaction, setId, input, token).ConfigureAwait(false) ?? throw SetNotFound(setId);
            if (set.ArchivedAt is not null) throw SetArchived(setId);
            var added = new List<Guid>();
            var duplicates = new List<Guid>();
            var fresh = new List<ImportedBackground>();
            await using (var exists = Command(connection, transaction, "SELECT set_id FROM background_items WHERE id = $id;"))
            await using (var pixels = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM background_items WHERE set_id = $set AND pixel_hash = $hash);"))
            {
                var id = exists.Parameters.Add("$id", SqliteType.Text);
                pixels.Parameters.AddWithValue("$set", Key(setId));
                var hash = pixels.Parameters.Add("$hash", SqliteType.Text);
                var batchHashes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    id.Value = Key(item.Id);
                    if (await exists.ExecuteScalarAsync(token).ConfigureAwait(false) is string)
                    {
                        added.Add(item.Id);
                        continue;
                    }
                    hash.Value = item.PixelHash;
                    if (!batchHashes.Add(item.PixelHash) || await ScalarAsync(pixels, token).ConfigureAwait(false) == 1)
                    {
                        duplicates.Add(item.Id);
                        continue;
                    }
                    fresh.Add(item);
                }
            }
            if (fresh.Count > 0)
            {
                await using (var reserve = Command(connection, transaction,
                    "UPDATE background_sets SET next_ordinal = next_ordinal + $count WHERE id = $id RETURNING next_ordinal - $count;"))
                {
                    reserve.Parameters.AddWithValue("$id", Key(setId));
                    reserve.Parameters.AddWithValue("$count", fresh.Count);
                    var first = await ScalarAsync(reserve, token).ConfigureAwait(false);
                    await InsertItemsAsync(connection, transaction, setId, fresh, first, token).ConfigureAwait(false);
                }
                await using var counters = Command(connection, transaction, """
                    UPDATE background_sets SET item_count = item_count + $count, content_revision = content_revision + 1, updated_at = $now WHERE id = $id;
                    """);
                counters.Parameters.AddWithValue("$id", Key(setId));
                counters.Parameters.AddWithValue("$count", fresh.Count);
                counters.Parameters.AddWithValue("$now", Time(Now()));
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                added.AddRange(fresh.Select(item => item.Id));
            }
            return (new BackgroundBatchOutcome(added, duplicates), fresh.Count);
        }, cancellationToken).ConfigureAwait(false);
        if (inserted > 0 && raise) NotifyItemsChanged(setId);
        return outcome;
    }

    /// <summary>
    /// What generation needs of these sets, read in one transaction: each existing set with its size, archive, content revision
    /// and active backgrounds in the order of addition (missing sets are left out). Not gated on <c>isOpen</c>, like the run target.
    /// </summary>
    internal Task<IReadOnlyList<BackgroundSourceSet>> ReadGenerationSourcesAsync(IReadOnlyList<Guid> setIds, CancellationToken cancellationToken)
        => store.ReadAsync<IReadOnlyList<BackgroundSourceSet>>(async (connection, token) =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await using var items = Command(connection, transaction, "SELECT id FROM background_items WHERE set_id = $set AND removed = 0 ORDER BY ordinal;");
            var set = items.Parameters.Add("$set", SqliteType.Text);
            var result = new List<BackgroundSourceSet>(setIds.Count);
            foreach (var setId in setIds)
            {
                if (await FindSetAsync(connection, transaction, setId, projectInput(), token).ConfigureAwait(false) is not { } found) continue;
                set.Value = Key(setId);
                var ids = await ListAsync(items, reader => ParseId(reader.GetString(0)), token).ConfigureAwait(false);
                result.Add(new BackgroundSourceSet(found.Id, found.Name, found.TargetSize, found.ArchivedAt is not null, found.ContentRevision, ids));
            }
            return result;
        }, cancellationToken);

    /// <summary>The set's items and counters changed; raised by a run at its own pace.</summary>
    internal void NotifyItemsChanged(Guid setId) => Raise(changedIn: [setId]);

    /// <summary>Extension of a stored background for the resource channel; null when the row does not exist.</summary>
    internal static async ValueTask<string?> FindExtensionAsync(ProjectStore store, MaterialFiles files, Guid id, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT EXISTS (SELECT 1 FROM background_items WHERE id = $id);");
            command.Parameters.AddWithValue("$id", Key(id));
            return await ScalarAsync(command, token).ConfigureAwait(false) == 1 ? FileExtension : null;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Rows of new backgrounds from <paramref name="firstOrdinal"/>; also used by checks that build large sets.</summary>
    internal static async Task InsertItemsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid setId, IReadOnlyList<ImportedBackground> items,
        long firstOrdinal, CancellationToken cancellationToken)
    {
        await using var insert = Command(connection, transaction, """
            INSERT INTO background_items (id, set_id, ordinal, width, height, byte_size, sha256, pixel_hash, signature, revision, created_at, removed_at,
                operation_id, origin, source_name, source_hash, video_name, timestamp_ms, frame_index, crop_left, crop_top, blur_radius, processor_version)
            VALUES ($id, $set, $ordinal, $width, $height, $size, $sha, $pixels, $signature, 1, $now, NULL, $operation, $origin, $name, $source, $video,
                $timestamp, $frame, $left, $top, $blur, $version);
            """);
        insert.Parameters.AddWithValue("$set", Key(setId));
        insert.Parameters.AddWithValue("$now", Time(Now()));
        var parameters = new[] { "$id", "$ordinal", "$width", "$height", "$size", "$sha", "$pixels", "$signature", "$operation", "$origin", "$name", "$source",
            "$video", "$timestamp", "$frame", "$left", "$top", "$blur", "$version" }
            // The type of each parameter follows its value: text, integer, real or the signature blob.
            .ToDictionary(name => name, name => insert.Parameters.Add(new SqliteParameter { ParameterName = name }));
        var position = firstOrdinal;
        foreach (var item in items)
        {
            var origin = item.Origin;
            parameters["$id"].Value = Key(item.Id);
            parameters["$ordinal"].Value = position++;
            parameters["$width"].Value = item.Width;
            parameters["$height"].Value = item.Height;
            parameters["$size"].Value = item.ByteSize;
            parameters["$sha"].Value = item.Sha256;
            parameters["$pixels"].Value = item.PixelHash;
            parameters["$signature"].Value = item.Signature;
            parameters["$operation"].Value = origin.OperationId;
            parameters["$origin"].Value = OriginKey(origin.Kind);
            parameters["$name"].Value = origin.SourceName;
            parameters["$source"].Value = origin.SourceHash;
            parameters["$video"].Value = (object?)origin.VideoName ?? DBNull.Value;
            parameters["$timestamp"].Value = (object?)origin.TimestampMs ?? DBNull.Value;
            parameters["$frame"].Value = (object?)origin.FrameIndex ?? DBNull.Value;
            parameters["$left"].Value = (object?)origin.Crop?.Left ?? DBNull.Value;
            parameters["$top"].Value = (object?)origin.Crop?.Top ?? DBNull.Value;
            parameters["$blur"].Value = (object?)origin.BlurRadius ?? DBNull.Value;
            parameters["$version"].Value = origin.ProcessorVersion;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- Deletion ----

    private sealed record Composition(string SetName, long Items, string Revision);

    private async Task<BackgroundDeletionReview> ReviewDeletionAsync(Guid setId, IReadOnlyList<Guid>? itemIds, CancellationToken cancellationToken)
    {
        RequireOpen();
        var composition = await store.ReadAsync((connection, token) => ComposeAsync(connection, null, setId, itemIds, token), cancellationToken)
            .ConfigureAwait(false);
        var found = await blockers.CollectAsync(projectId, store, setId, itemIds, cancellationToken).ConfigureAwait(false);
        return new BackgroundDeletionReview(setId, composition.SetName, itemIds is null, composition.Items, found, composition.Revision);
    }

    private async Task<BackgroundDeletionResult> DeleteAsync(Guid setId, IReadOnlyList<Guid>? itemIds, string reviewRevision, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRevision);
        RequireOpen();
        var found = await blockers.CollectAsync(projectId, store, setId, itemIds, cancellationToken).ConfigureAwait(false);
        if (found.Count > 0)
        {
            throw new CoreException("backgrounds.deletion_blocked",
                $"Deletion in background set {setId} is blocked: {string.Join(", ", found.Select(blocker => blocker.Code))}.");
        }
        var (composition, files) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var composition = await ComposeAsync(connection, transaction, setId, itemIds, token).ConfigureAwait(false);
            if (composition.Revision != reviewRevision)
                throw new CoreException("backgrounds.deletion_revision_conflict", $"Deletion review of background set {setId} is outdated.");
            var filter = itemIds is null ? "set_id = $set" : "id IN (SELECT value FROM json_each($ids))";
            List<(QueuedFile File, bool Removed)> files;
            await using (var read = Command(connection, transaction, $"SELECT id, removed FROM background_items WHERE {filter};"))
            {
                Bind(read, setId, itemIds);
                files = await ListAsync(read, reader => (new QueuedFile(FileKind, ParseId(reader.GetString(0)), FileExtension), reader.GetInt64(1) == 1), token)
                    .ConfigureAwait(false);
            }
            await FileCleanup.QueueAsync(connection, transaction, files.Select(item => item.File), token).ConfigureAwait(false);
            if (itemIds is null)
            {
                await JobsTable.DeleteFinishedByTargetAsync(connection, transaction, ImportBackgroundsHandler.Kind, setId, token).ConfigureAwait(false);
                // Items go with the set through ON DELETE CASCADE.
                await using var delete = Command(connection, transaction, "DELETE FROM background_sets WHERE id = $set;");
                delete.Parameters.AddWithValue("$set", Key(setId));
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else
            {
                await using (var delete = Command(connection, transaction, $"DELETE FROM background_items WHERE {filter};"))
                {
                    Bind(delete, setId, itemIds);
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                var removedCount = files.Count(item => item.Removed);
                await using var counters = Command(connection, transaction, """
                    UPDATE background_sets SET item_count = item_count - $active, removed_count = removed_count - $removed,
                        content_revision = content_revision + 1, updated_at = $now WHERE id = $set;
                    """);
                counters.Parameters.AddWithValue("$set", Key(setId));
                counters.Parameters.AddWithValue("$active", files.Count - removedCount);
                counters.Parameters.AddWithValue("$removed", removedCount);
                counters.Parameters.AddWithValue("$now", Time(Now()));
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return (composition, files.Select(item => item.File).ToArray());
        }, cancellationToken).ConfigureAwait(false);

        if (itemIds is null) Raise(sets: [new(setId, null)]);
        else Raise(items: itemIds.Select(id => new BackgroundsEntityChange(id, null)).ToArray(), changedIn: [setId]);
        logger.LogInformation("Project {ProjectId} background set {SetId}: {Items} backgrounds deleted{Whole}.", projectId, setId, composition.Items,
            itemIds is null ? " with the set" : "");
        // Files go in the background; the deletion already succeeded, so cleanup problems only leave files queued.
        var pending = await cleanup.RemoveAfterCommitAsync(files).ConfigureAwait(false);
        return new BackgroundDeletionResult(setId, itemIds is null, composition.Items, pending);
    }

    /// <summary>
    /// Count and digest of what is deleted: for a set its revision, content revision and every item (count, last ordinal, sum
    /// of revisions); for selected backgrounds each ID with its revision. Every selected background must belong to the set.
    /// </summary>
    private async Task<Composition> ComposeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid setId, IReadOnlyList<Guid>? itemIds,
        CancellationToken cancellationToken)
    {
        var set = await FindSetAsync(connection, transaction, setId, projectInput(), cancellationToken).ConfigureAwait(false) ?? throw SetNotFound(setId);
        var digest = new StringBuilder().Append(Key(setId)).Append('|');
        if (itemIds is null)
        {
            await using var stats = Command(connection, transaction,
                "SELECT count(*), coalesce(max(ordinal), 0), coalesce(sum(revision), 0) FROM background_items WHERE set_id = $set;");
            stats.Parameters.AddWithValue("$set", Key(setId));
            await using var reader = await stats.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var count = reader.GetInt64(0);
            digest.Append(set.Revision).Append('|').Append(set.ContentRevision).Append('|').Append(count).Append(':').Append(reader.GetInt64(1))
                .Append(':').Append(reader.GetInt64(2));
            return new Composition(set.Name, count, Digest(digest));
        }
        await using var command = Command(connection, transaction,
            "SELECT id, set_id, revision FROM background_items WHERE id IN (SELECT value FROM json_each($ids)) ORDER BY id;");
        command.Parameters.AddWithValue("$ids", JsonIds(itemIds));
        var rows = await ListAsync(command, reader => (Id: reader.GetString(0), Set: reader.GetString(1), Revision: reader.GetInt64(2)), cancellationToken)
            .ConfigureAwait(false);
        var missing = itemIds.Select(Key).Except(rows.Where(row => row.Set == Key(setId)).Select(row => row.Id)).FirstOrDefault();
        if (missing is not null) throw ItemNotFound(ParseId(missing));
        foreach (var row in rows) digest.Append(row.Id).Append(':').Append(row.Revision).Append('|');
        return new Composition(set.Name, rows.Count, Digest(digest));
    }

    private static void Bind(SqliteCommand command, Guid setId, IReadOnlyList<Guid>? itemIds)
    {
        if (itemIds is null) command.Parameters.AddWithValue("$set", Key(setId));
        else command.Parameters.AddWithValue("$ids", JsonIds(itemIds));
    }

    private static string Digest(StringBuilder value) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())).AsSpan(0, 16));

    // ---- Sets and pages ----

    private sealed record SetUpdate(string Set, Action<SqliteCommand> Bind);

    private async Task<BackgroundSet> ChangeSetAsync(Guid setId, long expectedRevision, Func<BackgroundSet, SetUpdate?> change, CancellationToken cancellationToken)
    {
        RequireOpen();
        var input = projectInput();
        var (set, saved) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindSetAsync(connection, transaction, setId, input, token).ConfigureAwait(false) ?? throw SetNotFound(setId);
            if (current.Revision != expectedRevision)
                throw RevisionConflict($"Background set {setId} has revision {current.Revision}, expected {expectedRevision}.");
            if (change(current) is not { } update) return (current, false);
            await using var command = Command(connection, transaction,
                $"UPDATE background_sets SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id RETURNING {SetColumns};");
            command.Parameters.AddWithValue("$id", Key(setId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            update.Bind(command);
            return ((await SingleOrDefaultAsync(command, reader => ReadSet(reader, input), token).ConfigureAwait(false))!, true);
        }, cancellationToken).ConfigureAwait(false);
        if (saved) Raise(sets: [new(set.Id, set.Revision)]);
        return set;
    }

    private static async Task<(List<BackgroundItem> Page, string? Next)> ReadPageAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid setId,
        bool? removed, int limit, long after, string scope, CancellationToken cancellationToken)
    {
        var where = removed is null ? "set_id = $set" : "set_id = $set AND removed = $removed";
        await using var command = Command(connection, transaction,
            $"SELECT {ItemColumns} FROM background_items WHERE {where} AND ordinal > $after ORDER BY ordinal LIMIT $limit;");
        command.Parameters.AddWithValue("$set", Key(setId));
        if (removed is { } value) command.Parameters.AddWithValue("$removed", value ? 1 : 0);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var rows = await ListAsync(command, reader => (Item: ReadItem(reader), Ordinal: reader.GetInt64(OrdinalColumn)), cancellationToken).ConfigureAwait(false);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return (rows.Select(row => row.Item).ToList(), more ? FormatCursor(scope, rows[^1].Ordinal) : null);
    }

    private static (string Name, string Description) ValidateText(string? name, string? description)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new CoreException("backgrounds.name_invalid", $"Set name must contain between 1 and {MaxNameLength} characters.");
        var text = description?.Trim() ?? "";
        if (text.Length > MaxDescriptionLength)
            throw new CoreException("backgrounds.description_invalid", $"Set description must not exceed {MaxDescriptionLength} characters.");
        return (trimmed, text);
    }

    // ---- Helpers ----

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("backgrounds.project_not_open", $"Project {projectId} is not open; its backgrounds are not available.");
    }

    private static void ValidateIds(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || ids.Count > MaxSelection || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new CoreException("backgrounds.selection_invalid", $"A selection holds 1–{MaxSelection} distinct backgrounds.");
    }

    private static string JsonIds(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.Select(Key));

    /// <summary>Opaque to the UI: the filter it was made for and the last ordinal, so a cursor of another filter is refused.</summary>
    private static string FormatCursor(string scope, long ordinal)
        => Base64Url.EncodeToString(Encoding.ASCII.GetBytes(scope + ":" + ordinal.ToString(CultureInfo.InvariantCulture)));

    private static long ParseCursor(string cursor, string scope)
    {
        try
        {
            var text = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor));
            if (text.StartsWith(scope + ":", StringComparison.Ordinal)
                && long.TryParse(text.AsSpan(scope.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
            {
                return ordinal;
            }
        }
        catch (FormatException)
        {
        }
        throw new ArgumentException("Background cursor is invalid or belongs to another filter.", nameof(cursor));
    }

    private void Raise(IReadOnlyList<BackgroundsEntityChange>? sets = null, IReadOnlyList<BackgroundsEntityChange>? items = null, IReadOnlyList<Guid>? changedIn = null)
        => changed(new BackgroundsChangedEventArgs(projectId, sets ?? [], items ?? [], changedIn ?? []));

    private static CoreException SetNotFound(Guid id) => new("backgrounds.set_not_found", $"Background set {id} was not found.");

    private static CoreException ItemNotFound(Guid id) => new("backgrounds.item_not_found", $"Background {id} was not found.");

    private static CoreException SetArchived(Guid id) => new("backgrounds.set_archived", $"Background set {id} is archived; restore it first.");

    private static CoreException RevisionConflict(string message) => new("backgrounds.revision_conflict", message);
}
