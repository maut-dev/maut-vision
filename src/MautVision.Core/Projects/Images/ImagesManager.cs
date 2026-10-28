using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;
using static MautVision.Core.Projects.Images.ImagesSql;

namespace MautVision.Core.Projects.Images;

/// <summary>
/// An image prepared by a job: its file is already in place under files/images. Reviewed — revision 1 of its annotation is
/// reviewed with these objects (a generated frame, possibly empty); null — revision 1 is unreviewed without objects (an import).
/// </summary>
internal sealed record ImportedImage(Guid Id, string DisplayName, ImageFileFormat Format, int Width, int Height, long ByteSize, string Sha256,
    string PixelHash, ImageLineage Lineage, IReadOnlyList<AnnotationObject>? Reviewed = null);

/// <summary>Added — rows of this batch, including rows an earlier interrupted run of the same job already inserted.</summary>
internal sealed record ImageBatchOutcome(IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Duplicates);

/// <summary>
/// Image groups, images and annotations of one project: tables image_groups, images, image_annotations and
/// image_annotation_objects. Annotation references to the catalog are checked with the catalog's own SQL
/// (<see cref="CatalogReferences"/>) inside the write transaction.
/// </summary>
internal sealed class ImagesManager : IImagesManager
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 4000;
    internal const int MaxDisplayNameLength = 240;
    internal const int MaxPage = 200;
    internal const int MaxSelection = 400;
    internal const int MaxObjects = 1000;
    internal const double BoxTolerance = 1e-9;
    private const string Area = "images";
    private const string FileKind = ProjectImage.ResourceKind;

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Action<ImagesChangedEventArgs> changed;
    private readonly FileCleanup cleanup;
    private readonly ImageBlockers blockers;
    private readonly ILogger logger;

    internal ImagesManager(string projectId, ProjectStore store, Func<bool> isOpen, Action<ImagesChangedEventArgs> changed, FileCleanup cleanup,
        ImageBlockers blockers, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.changed = changed;
        this.cleanup = cleanup;
        this.blockers = blockers;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ImageGroup>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {GroupColumns} FROM image_groups ORDER BY created_at, id;");
            return (IReadOnlyList<ImageGroup>)await CatalogSql.ListAsync(command, ReadGroup, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageGroup> GetGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindGroupAsync(connection, null, groupId, token), cancellationToken).ConfigureAwait(false)
            ?? throw GroupNotFound(groupId);
    }

    public async Task<ImageGroup> CreateGroupAsync(string name, string description, ImageSplitHint splitHint, CancellationToken cancellationToken = default)
    {
        var text = ValidateText(name, description);
        var split = SplitKey(splitHint);
        RequireOpen();
        var group = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, $"""
                INSERT INTO image_groups (id, name, description, origin, split_hint, created_at, updated_at, revision)
                VALUES ($id, $name, $description, 'manual', $split, $now, $now, 1)
                RETURNING {GroupColumns};
                """);
            command.Parameters.AddWithValue("$id", Key(Guid.NewGuid()));
            command.Parameters.AddWithValue("$name", text.Name);
            command.Parameters.AddWithValue("$description", text.Description);
            command.Parameters.AddWithValue("$split", split);
            command.Parameters.AddWithValue("$now", Time(Now()));
            return (await SingleOrDefaultAsync(command, ReadGroup, token).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);
        Raise(groups: [new(group.Id, group.Revision)]);
        return group;
    }

    public Task<ImageGroup> UpdateGroupAsync(Guid groupId, string name, string description, ImageSplitHint splitHint, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var text = ValidateText(name, description);
        var split = SplitKey(splitHint);
        return ChangeGroupAsync(groupId, expectedRevision, current =>
        {
            if (current.ArchivedAt is not null) throw GroupArchived(groupId);
            if (current.Name == text.Name && current.Description == text.Description && current.SplitHint == splitHint) return null;
            return new GroupUpdate("name = $name, description = $description, split_hint = $split", command =>
            {
                command.Parameters.AddWithValue("$name", text.Name);
                command.Parameters.AddWithValue("$description", text.Description);
                command.Parameters.AddWithValue("$split", split);
            });
        }, cancellationToken);
    }

    public async Task<ImageGroup> SetGroupArchivedAsync(Guid groupId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        if (archived)
        {
            // Checked before the write: an import that starts right after still fails at its next batch on the archived group.
            var found = await blockers.CollectAsync(projectId, store, groupId, cancellationToken).ConfigureAwait(false);
            if (found.Any(blocker => blocker.Code == ImageBlockers.ImportActive))
                throw new CoreException("images.group_importing", $"Image group {groupId} is being imported into.");
            if (found.Any(blocker => blocker.Code == ImageBlockers.GenerationActive))
                throw new CoreException("images.group_busy", $"Image group {groupId} is being filled by a generation.");
        }
        return await ChangeGroupAsync(groupId, expectedRevision, current => (current.ArchivedAt is not null) == archived
            ? null
            : new GroupUpdate("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImagePage> ListImagesAsync(Guid groupId, ImageFilter filter, int limit = 100, string? after = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        var scope = FilterKey(filter);
        var cursor = after is null ? 0L : ParseCursor(after, scope);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var where = new StringBuilder("group_id = $group");
            if (filter.Archived is not null) where.Append(" AND archived = $archived");
            if (filter.ReviewState is not null) where.Append(" AND review_state = $review");
            await using var command = Command(connection, null,
                $"SELECT {ImageColumns} FROM images WHERE {where} AND ordinal > $after ORDER BY ordinal LIMIT $limit;");
            command.Parameters.AddWithValue("$group", Key(groupId));
            if (filter.Archived is { } archived) command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            if (filter.ReviewState is { } review) command.Parameters.AddWithValue("$review", ReviewKey(review));
            command.Parameters.AddWithValue("$after", cursor);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var page = await CatalogSql.ListAsync(command, reader => (Image: ReadImage(reader), Ordinal: reader.GetInt64(24)), token).ConfigureAwait(false);
            if (page.Count == 0 && await FindGroupAsync(connection, null, groupId, token).ConfigureAwait(false) is null) throw GroupNotFound(groupId);
            var more = page.Count > limit;
            if (more) page.RemoveAt(page.Count - 1);
            return new ImagePage(page.Select(item => item.Image).ToArray(), more ? FormatCursor(scope, page[^1].Ordinal) : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectImage> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindImageAsync(connection, null, imageId, token), cancellationToken).ConfigureAwait(false)
            ?? throw ImageNotFound(imageId);
    }

    public async Task<ImageAnnotation> GetAnnotationAsync(Guid imageId, long? revision = null, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var image = await FindImageAsync(connection, null, imageId, token).ConfigureAwait(false) ?? throw ImageNotFound(imageId);
            var annotations = await ReadAnnotationsAsync(connection, null, [(imageId, revision ?? image.AnnotationRevision)], token).ConfigureAwait(false);
            return annotations.Count == 1
                ? annotations[0]
                : throw new CoreException("images.annotation_not_found", $"Annotation revision {revision} of image {imageId} does not exist.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ImageAnnotation>> ListAnnotationsAsync(IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageIds);
        if (imageIds.Count > MaxPage) throw SelectionInvalid($"At most {MaxPage} images per annotation page.");
        RequireOpen();
        if (imageIds.Count == 0) return [];
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null,
                "SELECT id, annotation_revision FROM images WHERE id IN (SELECT value FROM json_each($ids));");
            command.Parameters.AddWithValue("$ids", JsonIds(imageIds));
            var current = await CatalogSql.ListAsync(command, reader => (ParseId(reader.GetString(0)), reader.GetInt64(1)), token).ConfigureAwait(false);
            var found = await ReadAnnotationsAsync(connection, null, current, token).ConfigureAwait(false);
            // The caller's order: the page order of the gallery.
            var byId = found.ToDictionary(item => item.ImageId);
            return (IReadOnlyList<ImageAnnotation>)imageIds.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedAnnotation> SaveAnnotationAsync(Guid imageId, AnnotationInput annotation, long expectedAnnotationRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(annotation.Objects);
        if (!Enum.IsDefined(annotation.ReviewState)) throw new ArgumentOutOfRangeException(nameof(annotation), "Unknown review state.");
        if (annotation.Objects.Count > MaxObjects)
            throw new CoreException("images.annotation_invalid", $"An annotation holds at most {MaxObjects} objects.");
        if (annotation.Roi is { } roi && !IsValidBox(roi)) throw BoxInvalid("ROI");
        foreach (var item in annotation.Objects)
        {
            if (item is null) throw new ArgumentException("Annotation objects must not be null.", nameof(annotation));
            if (!IsValidBox(item.Box)) throw BoxInvalid("object");
        }
        RequireOpen();
        var saved = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var image = await FindImageAsync(connection, transaction, imageId, token).ConfigureAwait(false) ?? throw ImageNotFound(imageId);
            var group = (await FindGroupAsync(connection, transaction, image.GroupId, token).ConfigureAwait(false))!;
            RequireEditable(group);
            if (image.ArchivedAt is not null) throw new CoreException("images.image_archived", $"Image {imageId} is archived.");
            if (image.AnnotationRevision != expectedAnnotationRevision)
                throw RevisionConflict($"Image {imageId} annotation has revision {image.AnnotationRevision}, expected {expectedAnnotationRevision}.");
            var previous = (await ReadAnnotationsAsync(connection, transaction, [(imageId, image.AnnotationRevision)], token).ConfigureAwait(false))
                .SingleOrDefault()?.Objects.ToDictionary(item => item.Id) ?? [];

            var seen = new HashSet<Guid>();
            var checkedReferences = new Dictionary<(Guid, Guid?, bool), bool>();
            var objects = new List<AnnotationObject>(annotation.Objects.Count);
            foreach (var item in annotation.Objects)
            {
                if (item.Id is { } id && (!previous.ContainsKey(id) || !seen.Add(id)))
                    throw new CoreException("images.object_id_invalid", $"Object {id} is not in the current annotation of image {imageId} or repeats.");
                // An object whose class and variant did not change may keep an archived reference; new and changed ones may not.
                var unchanged = item.Id is { } kept && previous[kept].ClassId == item.ClassId && previous[kept].ClassVariantId == item.ClassVariantId;
                var key = (item.ClassId, item.ClassVariantId, !unchanged);
                if (!checkedReferences.TryGetValue(key, out var valid))
                {
                    valid = await CatalogReferences.IsValidAsync(connection, transaction, item.ClassId, item.ClassVariantId, requireActive: !unchanged, token)
                        .ConfigureAwait(false);
                    checkedReferences[key] = valid;
                }
                if (!valid)
                {
                    throw new CoreException("images.reference_unavailable",
                        $"Class {item.ClassId} with class variant {item.ClassVariantId} is missing, archived or does not belong together.");
                }
                objects.Add(new AnnotationObject(item.Id ?? Guid.NewGuid(), item.ClassId, item.ClassVariantId, item.Box));
            }

            var revision = image.AnnotationRevision + 1;
            var created = Now();
            await InsertAnnotationAsync(connection, transaction, imageId, revision, objects, annotation.Roi, annotation.ReviewState, created, token)
                .ConfigureAwait(false);
            await using (var update = Command(connection, transaction, """
                UPDATE images SET revision = revision + 1, annotation_revision = $revision, object_count = $count, review_state = $review
                WHERE id = $id;
                """))
            {
                update.Parameters.AddWithValue("$id", Key(imageId));
                update.Parameters.AddWithValue("$revision", revision);
                update.Parameters.AddWithValue("$count", objects.Count);
                update.Parameters.AddWithValue("$review", ReviewKey(annotation.ReviewState));
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var updated = (await FindImageAsync(connection, transaction, imageId, token).ConfigureAwait(false))!;
            return new SavedAnnotation(updated, new ImageAnnotation(imageId, revision, objects, annotation.Roi, annotation.ReviewState, created));
        }, cancellationToken).ConfigureAwait(false);
        Raise(images: [new(imageId, saved.Image.Revision)], changedIn: [saved.Image.GroupId]);
        return saved;
    }

    public async Task<IReadOnlyList<ProjectImage>> SetImagesArchivedAsync(IReadOnlyList<ImageRevision> images, bool archived,
        CancellationToken cancellationToken = default)
    {
        ValidateSelection(images);
        RequireOpen();
        var result = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var groups = new Dictionary<Guid, ImageGroup>();
            var updated = new List<ProjectImage>(images.Count);
            await using var update = Command(connection, transaction, "UPDATE images SET archived_at = $archived, revision = revision + 1 WHERE id = $id;");
            var id = update.Parameters.Add("$id", SqliteType.Text);
            update.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value);
            var delta = new Dictionary<Guid, long>();
            foreach (var item in images)
            {
                var image = await FindImageAsync(connection, transaction, item.Id, token).ConfigureAwait(false) ?? throw ImageNotFound(item.Id);
                if (!groups.TryGetValue(image.GroupId, out var group))
                {
                    group = (await FindGroupAsync(connection, transaction, image.GroupId, token).ConfigureAwait(false))!;
                    RequireEditable(group);
                    groups[group.Id] = group;
                }
                if (image.Revision != item.Revision) throw RevisionConflict($"Image {item.Id} has revision {image.Revision}, expected {item.Revision}.");
                if ((image.ArchivedAt is not null) == archived)
                {
                    updated.Add(image);
                    continue;
                }
                id.Value = Key(image.Id);
                await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                delta[image.GroupId] = delta.GetValueOrDefault(image.GroupId) + (archived ? 1 : -1);
                updated.Add((await FindImageAsync(connection, transaction, image.Id, token).ConfigureAwait(false))!);
            }
            await using var counters = Command(connection, transaction, "UPDATE image_groups SET archived_image_count = archived_image_count + $delta WHERE id = $id;");
            var groupId = counters.Parameters.Add("$id", SqliteType.Text);
            var change = counters.Parameters.Add("$delta", SqliteType.Integer);
            foreach (var (owner, value) in delta)
            {
                groupId.Value = Key(owner);
                change.Value = value;
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return (updated, delta.Keys.ToArray());
        }, cancellationToken).ConfigureAwait(false);
        if (result.Item2.Length > 0)
        {
            Raise(images: result.updated.Select(image => new ImagesEntityChange(image.Id, image.Revision)).ToArray(), changedIn: result.Item2);
        }
        return result.updated;
    }

    public async Task<MovedImages> MoveImagesAsync(Guid sourceGroupId, long sourceRevision, Guid targetGroupId, long targetRevision,
        IReadOnlyList<ImageRevision> images, CancellationToken cancellationToken = default)
    {
        ValidateSelection(images);
        if (sourceGroupId == targetGroupId) throw SelectionInvalid("Images are moved to another group.");
        RequireOpen();
        var moved = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var source = await FindGroupAsync(connection, transaction, sourceGroupId, token).ConfigureAwait(false) ?? throw GroupNotFound(sourceGroupId);
            var target = await FindGroupAsync(connection, transaction, targetGroupId, token).ConfigureAwait(false) ?? throw GroupNotFound(targetGroupId);
            RequireEditable(source);
            RequireEditable(target);
            if (source.Revision != sourceRevision) throw RevisionConflict($"Image group {sourceGroupId} has revision {source.Revision}, expected {sourceRevision}.");
            if (target.Revision != targetRevision) throw RevisionConflict($"Image group {targetGroupId} has revision {target.Revision}, expected {targetRevision}.");
            if (source.Origin != target.Origin)
                throw new CoreException("images.origin_mismatch", $"Images move only between groups of the same origin ({source.Origin} → {target.Origin}).");
            var found = new List<(ProjectImage Image, long Ordinal)>(images.Count);
            await using (var read = Command(connection, transaction, $"SELECT {ImageColumns} FROM images WHERE id = $id;"))
            {
                var id = read.Parameters.Add("$id", SqliteType.Text);
                foreach (var item in images)
                {
                    id.Value = Key(item.Id);
                    var row = await CatalogSql.ListAsync(read, reader => (ReadImage(reader), reader.GetInt64(24)), token).ConfigureAwait(false);
                    if (row.Count == 0) throw ImageNotFound(item.Id);
                    var (image, ordinal) = row[0];
                    if (image.GroupId != sourceGroupId) throw SelectionInvalid($"Image {item.Id} is not in group {sourceGroupId}.");
                    if (image.ArchivedAt is not null) throw new CoreException("images.image_archived", $"Image {item.Id} is archived.");
                    if (image.Revision != item.Revision) throw RevisionConflict($"Image {item.Id} has revision {image.Revision}, expected {item.Revision}.");
                    found.Add((image, ordinal));
                }
            }
            var next = await ReserveOrdinalsAsync(connection, transaction, targetGroupId, found.Count, token).ConfigureAwait(false);
            await using (var update = Command(connection, transaction, "UPDATE images SET group_id = $group, ordinal = $ordinal, revision = revision + 1 WHERE id = $id;"))
            {
                update.Parameters.AddWithValue("$group", Key(targetGroupId));
                var id = update.Parameters.Add("$id", SqliteType.Text);
                var ordinal = update.Parameters.Add("$ordinal", SqliteType.Integer);
                // The source order is kept at the end of the target.
                foreach (var item in found.OrderBy(entry => entry.Ordinal))
                {
                    id.Value = Key(item.Image.Id);
                    ordinal.Value = next++;
                    await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            await using (var counters = Command(connection, transaction, """
                UPDATE image_groups SET image_count = image_count + $delta, revision = revision + 1, updated_at = $now WHERE id = $id;
                """))
            {
                counters.Parameters.AddWithValue("$now", Time(Now()));
                var id = counters.Parameters.Add("$id", SqliteType.Text);
                var delta = counters.Parameters.Add("$delta", SqliteType.Integer);
                foreach (var (group, value) in new[] { (sourceGroupId, -found.Count), (targetGroupId, found.Count) })
                {
                    id.Value = Key(group);
                    delta.Value = value;
                    await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            var updated = new List<ProjectImage>(found.Count);
            foreach (var item in images) updated.Add((await FindImageAsync(connection, transaction, item.Id, token).ConfigureAwait(false))!);
            return new MovedImages((await FindGroupAsync(connection, transaction, sourceGroupId, token).ConfigureAwait(false))!,
                (await FindGroupAsync(connection, transaction, targetGroupId, token).ConfigureAwait(false))!, updated);
        }, cancellationToken).ConfigureAwait(false);
        Raise(groups: [new(moved.Source.Id, moved.Source.Revision), new(moved.Target.Id, moved.Target.Revision)],
            images: moved.Images.Select(image => new ImagesEntityChange(image.Id, image.Revision)).ToArray(),
            changedIn: [sourceGroupId, targetGroupId]);
        logger.LogInformation("Project {ProjectId}: {Count} images moved from group {Source} to {Target}.", projectId, moved.Images.Count,
            sourceGroupId, targetGroupId);
        return moved;
    }

    public async Task<IReadOnlyList<JobInfo>> ListImportsAsync(Guid groupId, int limit = 50, Guid? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            if (await FindGroupAsync(connection, null, groupId, token).ConfigureAwait(false) is null) throw GroupNotFound(groupId);
            return await JobsTable.ListByTargetAsync(connection, projectId, ImportImagesHandler.Kind, groupId, limit, before, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ImageDeletionReview> ReviewGroupDeletionAsync(Guid groupId, CancellationToken cancellationToken = default)
        => ReviewDeletionAsync(groupId, null, cancellationToken);

    public Task<ImageDeletionResult> DeleteGroupAsync(Guid groupId, string reviewRevision, CancellationToken cancellationToken = default)
        => DeleteAsync(groupId, null, reviewRevision, cancellationToken);

    public Task<ImageDeletionReview> ReviewImagesDeletionAsync(Guid groupId, IReadOnlyList<Guid> imageIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(imageIds);
        return ReviewDeletionAsync(groupId, imageIds, cancellationToken);
    }

    public Task<ImageDeletionResult> DeleteImagesAsync(Guid groupId, IReadOnlyList<Guid> imageIds, string reviewRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateIds(imageIds);
        return DeleteAsync(groupId, imageIds, reviewRevision, cancellationToken);
    }

    // ---- Import job support ----

    /// <summary>
    /// The import writes only into an existing active manual group; anything else ends the job. Not gated on <c>isOpen</c>:
    /// recovered jobs are dispatched while the project is still being opened, after its store is ready.
    /// </summary>
    internal async Task<ImageGroup> RequireImportTargetAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var group = await store.ReadAsync((connection, token) => FindGroupAsync(connection, null, groupId, token), cancellationToken)
            .ConfigureAwait(false) ?? throw GroupNotFound(groupId);
        RequireImportable(group);
        return group;
    }

    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => store.GetFilesAsync(cancellationToken);

    /// <summary>Pixel hashes of this list that other images of the group (not the given IDs) already have; indexed lookups.</summary>
    internal Task<HashSet<string>> FindPixelHashesAsync(Guid groupId, IReadOnlyCollection<string> hashes, IReadOnlyCollection<Guid> exceptIds,
        CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (hashes.Count == 0) return result;
            await using var command = Command(connection, null, """
                SELECT DISTINCT pixel_hash FROM images WHERE group_id = $group AND pixel_hash IN (SELECT value FROM json_each($hashes))
                    AND id NOT IN (SELECT value FROM json_each($except));
                """);
            command.Parameters.AddWithValue("$group", Key(groupId));
            command.Parameters.AddWithValue("$hashes", JsonSerializer.Serialize(hashes));
            command.Parameters.AddWithValue("$except", JsonIds(exceptIds));
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(reader.GetString(0));
            return result;
        }, cancellationToken);

    /// <summary>
    /// Inserts one batch in one transaction, in the given order at the end of the group. A row with the same ID is a replay
    /// of an interrupted run and counts as added; with skipDuplicates the same pixels already in the group (committed by
    /// another import meanwhile or earlier in this batch) make a duplicate, whose file the caller removes.
    /// raise: false lets the caller throttle the change event (<see cref="NotifyImagesChanged"/>).
    /// </summary>
    internal async Task<ImageBatchOutcome> InsertImportedAsync(Guid groupId, IReadOnlyList<ImportedImage> images, string operationId, bool skipDuplicates,
        bool raise, CancellationToken cancellationToken)
    {
        if (images.Count == 0) return new ImageBatchOutcome([], []);
        var (outcome, inserted) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var group = await FindGroupAsync(connection, transaction, groupId, token).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
            RequireImportable(group);
            var added = new List<Guid>();
            var duplicates = new List<Guid>();
            var fresh = new List<ImportedImage>();
            await using (var exists = Command(connection, transaction, "SELECT group_id FROM images WHERE id = $id;"))
            await using (var pixels = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM images WHERE group_id = $group AND pixel_hash = $hash);"))
            {
                var id = exists.Parameters.Add("$id", SqliteType.Text);
                pixels.Parameters.AddWithValue("$group", Key(groupId));
                var hash = pixels.Parameters.Add("$hash", SqliteType.Text);
                var batchHashes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var image in images)
                {
                    id.Value = Key(image.Id);
                    if (await exists.ExecuteScalarAsync(token).ConfigureAwait(false) is string)
                    {
                        added.Add(image.Id);
                        continue;
                    }
                    if (skipDuplicates)
                    {
                        hash.Value = image.PixelHash;
                        if (!batchHashes.Add(image.PixelHash) || await ScalarAsync(pixels, token).ConfigureAwait(false) == 1)
                        {
                            duplicates.Add(image.Id);
                            continue;
                        }
                    }
                    fresh.Add(image);
                }
            }
            if (fresh.Count > 0)
            {
                var next = await ReserveOrdinalsAsync(connection, transaction, groupId, fresh.Count, token).ConfigureAwait(false);
                await InsertImagesAsync(connection, transaction, groupId, fresh, next, operationId, token).ConfigureAwait(false);
                await using var counters = Command(connection, transaction, "UPDATE image_groups SET image_count = image_count + $count WHERE id = $id;");
                counters.Parameters.AddWithValue("$id", Key(groupId));
                counters.Parameters.AddWithValue("$count", fresh.Count);
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                added.AddRange(fresh.Select(image => image.Id));
            }
            return (new ImageBatchOutcome(added, duplicates), fresh.Count);
        }, cancellationToken).ConfigureAwait(false);
        if (inserted > 0 && raise) NotifyImagesChanged(groupId);
        return outcome;
    }

    /// <summary>The group's images and counters changed; raised by the import at its own pace.</summary>
    internal void NotifyImagesChanged(Guid groupId) => Raise(changedIn: [groupId]);

    // ---- Dataset support ----

    /// <summary>Active images of the groups with their current annotations, for a dataset plan; not gated on <c>isOpen</c> (recovered builds).</summary>
    internal Task<ImageSourceSet> ReadDatasetSourcesAsync(IReadOnlyList<Guid> groupIds, CancellationToken cancellationToken)
        => ImageSources.ReadAsync(store, groupIds, cancellationToken);

    /// <summary>The revision of the groups' images and annotations, compared with the revision a plan was made from.</summary>
    internal Task<string> ReadDatasetRevisionAsync(IReadOnlyList<Guid> groupIds, CancellationToken cancellationToken)
        => ImageSources.ReadRevisionAsync(store, groupIds, cancellationToken);

    // ---- Generation support ----

    /// <summary>
    /// The output group of a generation run: origin generated, state producing, split unassigned. A repeated preparation of the
    /// same run finds its group. Not gated on <c>isOpen</c>, like the import target (recovered runs start during the open).
    /// </summary>
    internal async Task<ImageGroup> CreateGeneratedGroupAsync(Guid groupId, string name, Guid runId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > MaxNameLength) throw new ArgumentException("A generated group name has 1–120 characters.", nameof(name));
        var (group, created) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            if (await FindGroupAsync(connection, transaction, groupId, token).ConfigureAwait(false) is { } existing)
            {
                return existing.GenerationRunId == runId
                    ? (existing, false)
                    : throw new InvalidOperationException($"Image group {groupId} belongs to another generation run.");
            }
            await using var command = Command(connection, transaction, $"""
                INSERT INTO image_groups (id, name, description, origin, split_hint, generation_run_id, generation_state, created_at, updated_at, revision)
                VALUES ($id, $name, '', 'generated', 'unassigned', $run, 'producing', $now, $now, 1)
                RETURNING {GroupColumns};
                """);
            command.Parameters.AddWithValue("$id", Key(groupId));
            command.Parameters.AddWithValue("$name", trimmed);
            command.Parameters.AddWithValue("$run", Key(runId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            return ((await SingleOrDefaultAsync(command, ReadGroup, token).ConfigureAwait(false))!, true);
        }, cancellationToken).ConfigureAwait(false);
        if (created) Raise(groups: [new(group.Id, group.Revision)]);
        return group;
    }

    /// <summary>
    /// Inserts generated frames in the given order at the end of the run's producing group in one transaction. A row with the same
    /// ID is a replay of an interrupted run; returns how many rows are new. raise: false lets the caller throttle the event.
    /// </summary>
    internal async Task<int> InsertGeneratedAsync(Guid groupId, Guid runId, IReadOnlyList<ImportedImage> images, string operationId, bool raise,
        CancellationToken cancellationToken)
    {
        if (images.Count == 0) return 0;
        var inserted = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var group = await FindGroupAsync(connection, transaction, groupId, token).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
            if (group.GenerationRunId != runId || group.GenerationState != ImageGenerationState.Producing)
                throw new InvalidOperationException($"Image group {groupId} is not being filled by generation run {runId}.");
            var fresh = new List<ImportedImage>(images.Count);
            await using (var exists = Command(connection, transaction, "SELECT EXISTS (SELECT 1 FROM images WHERE id = $id);"))
            {
                var id = exists.Parameters.Add("$id", SqliteType.Text);
                foreach (var image in images)
                {
                    id.Value = Key(image.Id);
                    if (await ScalarAsync(exists, token).ConfigureAwait(false) == 0) fresh.Add(image);
                }
            }
            if (fresh.Count == 0) return 0;
            var next = await ReserveOrdinalsAsync(connection, transaction, groupId, fresh.Count, token).ConfigureAwait(false);
            await InsertImagesAsync(connection, transaction, groupId, fresh, next, operationId, token).ConfigureAwait(false);
            await using var counters = Command(connection, transaction, "UPDATE image_groups SET image_count = image_count + $count WHERE id = $id;");
            counters.Parameters.AddWithValue("$id", Key(groupId));
            counters.Parameters.AddWithValue("$count", fresh.Count);
            await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return fresh.Count;
        }, cancellationToken).ConfigureAwait(false);
        if (inserted > 0 && raise) NotifyImagesChanged(groupId);
        return inserted;
    }

    /// <summary>Final state of a run's group: completed or partial. Only a producing group changes; its revision grows.</summary>
    internal async Task FinishGeneratedGroupAsync(Guid groupId, ImageGenerationState state, CancellationToken cancellationToken)
    {
        if (state == ImageGenerationState.Producing) throw new ArgumentOutOfRangeException(nameof(state), "A finished group is completed or partial.");
        var revision = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, """
                UPDATE image_groups SET generation_state = $state, revision = revision + 1, updated_at = $now
                WHERE id = $id AND generation_state = 'producing' RETURNING revision;
                """);
            command.Parameters.AddWithValue("$id", Key(groupId));
            command.Parameters.AddWithValue("$state", state == ImageGenerationState.Completed ? "completed" : "partial");
            command.Parameters.AddWithValue("$now", Time(Now()));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is long value ? value : (long?)null;
        }, cancellationToken).ConfigureAwait(false);
        if (revision is { } changed) Raise(groups: [new(groupId, changed)], changedIn: [groupId]);
    }

    /// <summary>
    /// Producing groups whose run is not queued or running any more (the final state was never written) become partial;
    /// called when the project opens, after its jobs are recovered.
    /// </summary>
    internal async Task<IReadOnlyList<Guid>> FinishOrphanedGenerationAsync(IReadOnlySet<Guid> activeRuns, CancellationToken cancellationToken)
    {
        var producing = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id, generation_run_id FROM image_groups WHERE generation_state = 'producing';");
            return await CatalogSql.ListAsync(command, reader => (Group: ParseId(reader.GetString(0)), Run: reader.IsDBNull(1) ? Guid.Empty : ParseId(reader.GetString(1))),
                token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        var orphaned = producing.Where(item => !activeRuns.Contains(item.Run)).Select(item => item.Group).ToArray();
        foreach (var group in orphaned) await FinishGeneratedGroupAsync(group, ImageGenerationState.Partial, cancellationToken).ConfigureAwait(false);
        return orphaned;
    }

    /// <summary>Deletion blocker of a group a generation is filling; the example is the group name.</summary>
    internal static Task<IReadOnlyList<DeletionBlocker>> GenerationBlockersAsync(ProjectStore store, Guid groupId, CancellationToken cancellationToken)
        => store.ReadAsync<IReadOnlyList<DeletionBlocker>>(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT name FROM image_groups WHERE id = $id AND generation_state = 'producing';");
            command.Parameters.AddWithValue("$id", Key(groupId));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string name
                ? [new DeletionBlocker(ImageBlockers.GenerationActive, 1, [name])]
                : [];
        }, cancellationToken);

    /// <summary>Extension of a stored image for the resource channel; null when the row does not exist.</summary>
    internal static async ValueTask<string?> FindExtensionAsync(ProjectStore store, MaterialFiles files, Guid id, CancellationToken cancellationToken)
        => await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT format FROM images WHERE id = $id;");
            command.Parameters.AddWithValue("$id", Key(id));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string format ? Extension(ImagesSql.ParseFormat(format)) : null;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Rows of new images with revision 1 of their annotation (no objects, unreviewed). Also used by tools that build
    /// large projects (the performance generator), so the rows always go through the real schema.
    /// </summary>
    internal static async Task InsertImagesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid groupId, IReadOnlyList<ImportedImage> images,
        long firstOrdinal, string operationId, CancellationToken cancellationToken)
    {
        var now = Time(Now());
        await using var insert = Command(connection, transaction, """
            INSERT INTO images (id, group_id, ordinal, display_name, format, width, height, byte_size, sha256, pixel_hash, revision,
                annotation_revision, object_count, review_state, created_at, archived_at, operation_id, lineage_kind, source_family_id,
                video_name, timestamp_ms, frame_index, generation_run_id, sample_index, recipe_revision)
            VALUES ($id, $group, $ordinal, $name, $format, $width, $height, $size, $sha, $pixels, 1, 1, $count, $review, $now, NULL, $operation,
                $lineage, $family, $video, $timestamp, $frame, $run, $sample, $recipe);
            """);
        insert.Parameters.AddWithValue("$group", Key(groupId));
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$operation", operationId);
        var id = insert.Parameters.Add("$id", SqliteType.Text);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var name = insert.Parameters.Add("$name", SqliteType.Text);
        var format = insert.Parameters.Add("$format", SqliteType.Text);
        var width = insert.Parameters.Add("$width", SqliteType.Integer);
        var height = insert.Parameters.Add("$height", SqliteType.Integer);
        var size = insert.Parameters.Add("$size", SqliteType.Integer);
        var sha = insert.Parameters.Add("$sha", SqliteType.Text);
        var pixels = insert.Parameters.Add("$pixels", SqliteType.Text);
        var lineage = insert.Parameters.Add("$lineage", SqliteType.Text);
        var family = insert.Parameters.Add("$family", SqliteType.Text);
        var video = insert.Parameters.Add("$video", SqliteType.Text);
        var timestamp = insert.Parameters.Add("$timestamp", SqliteType.Integer);
        var frame = insert.Parameters.Add("$frame", SqliteType.Integer);
        var run = insert.Parameters.Add("$run", SqliteType.Text);
        var sample = insert.Parameters.Add("$sample", SqliteType.Integer);
        var recipe = insert.Parameters.Add("$recipe", SqliteType.Integer);
        var count = insert.Parameters.Add("$count", SqliteType.Integer);
        var review = insert.Parameters.Add("$review", SqliteType.Text);
        await using var annotation = Command(connection, transaction,
            "INSERT INTO image_annotations (image_id, revision, review_state, object_count, created_at) VALUES ($id, 1, $review, $count, $now);");
        annotation.Parameters.AddWithValue("$now", now);
        var annotationId = annotation.Parameters.Add("$id", SqliteType.Text);
        var annotationReview = annotation.Parameters.Add("$review", SqliteType.Text);
        var annotationCount = annotation.Parameters.Add("$count", SqliteType.Integer);
        await using var objects = Command(connection, transaction, """
            INSERT INTO image_annotation_objects (image_id, revision, position, object_id, class_id, class_variant_id, x, y, width, height)
            VALUES ($id, 1, $position, $object, $class, $variant, $x, $y, $width, $height);
            """);
        var objectImage = objects.Parameters.Add("$id", SqliteType.Text);
        var objectPosition = objects.Parameters.Add("$position", SqliteType.Integer);
        var objectId = objects.Parameters.Add("$object", SqliteType.Text);
        var objectClass = objects.Parameters.Add("$class", SqliteType.Text);
        var objectVariant = objects.Parameters.Add("$variant", SqliteType.Text);
        var objectX = objects.Parameters.Add("$x", SqliteType.Real);
        var objectY = objects.Parameters.Add("$y", SqliteType.Real);
        var objectWidth = objects.Parameters.Add("$width", SqliteType.Real);
        var objectHeight = objects.Parameters.Add("$height", SqliteType.Real);
        var position = firstOrdinal;
        foreach (var image in images)
        {
            var reviewKey = ReviewKey(image.Reviewed is null ? ImageReviewState.Unreviewed : ImageReviewState.Reviewed);
            var objectCount = image.Reviewed?.Count ?? 0;
            count.Value = objectCount;
            review.Value = reviewKey;
            annotationReview.Value = reviewKey;
            annotationCount.Value = objectCount;
            id.Value = Key(image.Id);
            ordinal.Value = position++;
            name.Value = image.DisplayName;
            format.Value = FormatKey(image.Format);
            width.Value = image.Width;
            height.Value = image.Height;
            size.Value = image.ByteSize;
            sha.Value = image.Sha256;
            pixels.Value = image.PixelHash;
            lineage.Value = LineageKey(image.Lineage.Kind);
            family.Value = image.Lineage.SourceFamilyId;
            video.Value = (object?)image.Lineage.VideoName ?? DBNull.Value;
            timestamp.Value = (object?)image.Lineage.TimestampMs ?? DBNull.Value;
            frame.Value = (object?)image.Lineage.FrameIndex ?? DBNull.Value;
            run.Value = image.Lineage.GenerationRunId is { } runId ? Key(runId) : DBNull.Value;
            sample.Value = (object?)image.Lineage.SampleIndex ?? DBNull.Value;
            recipe.Value = (object?)image.Lineage.RecipeRevision ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            annotationId.Value = Key(image.Id);
            await annotation.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < objectCount; index++)
            {
                var item = image.Reviewed![index];
                objectImage.Value = Key(image.Id);
                objectPosition.Value = index;
                objectId.Value = Key(item.Id);
                objectClass.Value = Key(item.ClassId);
                objectVariant.Value = item.ClassVariantId is { } variant ? Key(variant) : DBNull.Value;
                objectX.Value = item.Box.X;
                objectY.Value = item.Box.Y;
                objectWidth.Value = item.Box.Width;
                objectHeight.Value = item.Box.Height;
                await objects.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Hands out <paramref name="count"/> ordinals at the end of the group; returns the first.</summary>
    internal static async Task<long> ReserveOrdinalsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid groupId, int count,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            "UPDATE image_groups SET next_ordinal = next_ordinal + $count WHERE id = $id RETURNING next_ordinal - $count;");
        command.Parameters.AddWithValue("$id", Key(groupId));
        command.Parameters.AddWithValue("$count", count);
        return await ScalarAsync(command, cancellationToken).ConfigureAwait(false);
    }

    // ---- Deletion ----

    private sealed record Composition(string GroupName, long Images, string Revision);

    private async Task<ImageDeletionReview> ReviewDeletionAsync(Guid groupId, IReadOnlyList<Guid>? imageIds, CancellationToken cancellationToken)
    {
        RequireOpen();
        var composition = await store.ReadAsync((connection, token) => ComposeAsync(connection, null, groupId, imageIds, token), cancellationToken)
            .ConfigureAwait(false);
        var found = await blockers.CollectAsync(projectId, store, groupId, cancellationToken).ConfigureAwait(false);
        return new ImageDeletionReview(groupId, composition.GroupName, imageIds is null, composition.Images, found, composition.Revision);
    }

    private async Task<ImageDeletionResult> DeleteAsync(Guid groupId, IReadOnlyList<Guid>? imageIds, string reviewRevision,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewRevision);
        RequireOpen();
        var found = await blockers.CollectAsync(projectId, store, groupId, cancellationToken).ConfigureAwait(false);
        if (found.Count > 0)
        {
            throw new CoreException("images.deletion_blocked",
                $"Deletion in image group {groupId} is blocked: {string.Join(", ", found.Select(blocker => blocker.Code))}.");
        }
        var (composition, files) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var composition = await ComposeAsync(connection, transaction, groupId, imageIds, token).ConfigureAwait(false);
            if (composition.Revision != reviewRevision)
                throw new CoreException("images.deletion_revision_conflict", $"Deletion review of image group {groupId} is outdated.");
            var filter = imageIds is null ? "group_id = $group" : "id IN (SELECT value FROM json_each($ids))";
            List<(QueuedFile File, bool Archived)> files;
            await using (var read = Command(connection, transaction, $"SELECT id, format, archived FROM images WHERE {filter};"))
            {
                Bind(read, groupId, imageIds);
                files = await CatalogSql.ListAsync(read, reader => (new QueuedFile(FileKind, ParseId(reader.GetString(0)), Extension(ImagesSql.ParseFormat(reader.GetString(1)))),
                    reader.GetInt64(2) == 1), token).ConfigureAwait(false);
            }
            await FileCleanup.QueueAsync(connection, transaction, files.Select(item => item.File), token).ConfigureAwait(false);
            if (imageIds is null)
            {
                await JobsTable.DeleteFinishedByTargetAsync(connection, transaction, ImportImagesHandler.Kind, groupId, token).ConfigureAwait(false);
                // Images, their annotations and annotation objects go with the group through ON DELETE CASCADE.
                await using var delete = Command(connection, transaction, "DELETE FROM image_groups WHERE id = $group;");
                delete.Parameters.AddWithValue("$group", Key(groupId));
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            else
            {
                await using (var delete = Command(connection, transaction, $"DELETE FROM images WHERE {filter};"))
                {
                    Bind(delete, groupId, imageIds);
                    await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                await using var counters = Command(connection, transaction, """
                    UPDATE image_groups SET image_count = image_count - $count, archived_image_count = archived_image_count - $archived WHERE id = $group;
                    """);
                counters.Parameters.AddWithValue("$group", Key(groupId));
                counters.Parameters.AddWithValue("$count", files.Count);
                counters.Parameters.AddWithValue("$archived", files.Count(item => item.Archived));
                await counters.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return (composition, files.Select(item => item.File).ToArray());
        }, cancellationToken).ConfigureAwait(false);

        if (imageIds is null)
        {
            Raise(groups: [new(groupId, null)]);
        }
        else
        {
            Raise(images: imageIds.Select(id => new ImagesEntityChange(id, null)).ToArray(), changedIn: [groupId]);
        }
        logger.LogInformation("Project {ProjectId} image group {GroupId}: {Images} images deleted{Whole}.", projectId, groupId, composition.Images,
            imageIds is null ? " with the group" : "");
        // Files go in the background; the deletion already succeeded, so cleanup problems only leave files queued.
        var pending = await cleanup.RemoveAfterCommitAsync(files).ConfigureAwait(false);
        return new ImageDeletionResult(groupId, imageIds is null, composition.Images, pending);
    }

    /// <summary>
    /// Counts and the digest of what is deleted: for a group its revision and every image (count, last ordinal, sum of
    /// revisions); for selected images each ID with its revision. Every selected image must belong to the group.
    /// </summary>
    private static async Task<Composition> ComposeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid groupId,
        IReadOnlyList<Guid>? imageIds, CancellationToken cancellationToken)
    {
        var group = await FindGroupAsync(connection, transaction, groupId, cancellationToken).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
        var digest = new StringBuilder().Append(Key(groupId)).Append('|');
        if (imageIds is null)
        {
            await using var stats = Command(connection, transaction,
                "SELECT count(*), coalesce(max(ordinal), 0), coalesce(sum(revision), 0) FROM images WHERE group_id = $group;");
            stats.Parameters.AddWithValue("$group", Key(groupId));
            await using var reader = await stats.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var count = reader.GetInt64(0);
            digest.Append(group.Revision).Append('|').Append(count).Append(':').Append(reader.GetInt64(1)).Append(':').Append(reader.GetInt64(2));
            return new Composition(group.Name, count, Digest(digest));
        }
        await using var command = Command(connection, transaction,
            "SELECT id, group_id, revision FROM images WHERE id IN (SELECT value FROM json_each($ids)) ORDER BY id;");
        command.Parameters.AddWithValue("$ids", JsonIds(imageIds));
        var rows = await CatalogSql.ListAsync(command, reader => (Id: reader.GetString(0), Group: reader.GetString(1), Revision: reader.GetInt64(2)),
            cancellationToken).ConfigureAwait(false);
        var missing = imageIds.Select(Key).Except(rows.Where(row => row.Group == Key(groupId)).Select(row => row.Id)).FirstOrDefault();
        if (missing is not null) throw ImageNotFound(ParseId(missing));
        foreach (var row in rows) digest.Append(row.Id).Append(':').Append(row.Revision).Append('|');
        return new Composition(group.Name, rows.Count, Digest(digest));
    }

    private static void Bind(SqliteCommand command, Guid groupId, IReadOnlyList<Guid>? imageIds)
    {
        if (imageIds is null) command.Parameters.AddWithValue("$group", Key(groupId));
        else command.Parameters.AddWithValue("$ids", JsonIds(imageIds));
    }

    private static string Digest(StringBuilder value)
        => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())).AsSpan(0, 16));

    // ---- Annotations ----

    private static async Task<List<ImageAnnotation>> ReadAnnotationsAsync(SqliteConnection connection, SqliteTransaction? transaction,
        IReadOnlyList<(Guid ImageId, long Revision)> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0) return [];
        var json = JsonSerializer.Serialize(keys.Select(key => new object[] { Key(key.ImageId), key.Revision }));
        var headers = new Dictionary<Guid, (long Revision, ImageReviewState Review, ImageBox? Roi, DateTimeOffset Created)>();
        await using (var command = Command(connection, transaction, """
            SELECT a.image_id, a.revision, a.review_state, a.roi_x, a.roi_y, a.roi_width, a.roi_height, a.created_at
            FROM json_each($keys) k JOIN image_annotations a ON a.image_id = json_extract(k.value, '$[0]') AND a.revision = json_extract(k.value, '$[1]');
            """))
        {
            command.Parameters.AddWithValue("$keys", json);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var roi = reader.IsDBNull(3) ? null : new ImageBox(reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6));
                headers[ParseId(reader.GetString(0))] = (reader.GetInt64(1), ParseReview(reader.GetString(2)), roi, Time(reader.GetInt64(7)));
            }
        }
        var objects = new Dictionary<Guid, List<AnnotationObject>>();
        await using (var command = Command(connection, transaction, """
            SELECT o.image_id, o.object_id, o.class_id, o.class_variant_id, o.x, o.y, o.width, o.height
            FROM json_each($keys) k JOIN image_annotation_objects o ON o.image_id = json_extract(k.value, '$[0]') AND o.revision = json_extract(k.value, '$[1]')
            ORDER BY o.image_id, o.position;
            """))
        {
            command.Parameters.AddWithValue("$keys", json);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var image = ParseId(reader.GetString(0));
                if (!objects.TryGetValue(image, out var list)) objects[image] = list = [];
                list.Add(new AnnotationObject(ParseId(reader.GetString(1)), ParseId(reader.GetString(2)), reader.IsDBNull(3) ? null : ParseId(reader.GetString(3)),
                    new ImageBox(reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7))));
            }
        }
        return headers.Select(entry => new ImageAnnotation(entry.Key, entry.Value.Revision,
            objects.TryGetValue(entry.Key, out var list) ? list : [], entry.Value.Roi, entry.Value.Review, entry.Value.Created)).ToList();
    }

    private static async Task InsertAnnotationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid imageId, long revision,
        IReadOnlyList<AnnotationObject> objects, ImageBox? roi, ImageReviewState review, DateTimeOffset created, CancellationToken cancellationToken)
    {
        await using (var header = Command(connection, transaction, """
            INSERT INTO image_annotations (image_id, revision, review_state, roi_x, roi_y, roi_width, roi_height, object_count, created_at)
            VALUES ($id, $revision, $review, $x, $y, $width, $height, $count, $now);
            """))
        {
            header.Parameters.AddWithValue("$id", Key(imageId));
            header.Parameters.AddWithValue("$revision", revision);
            header.Parameters.AddWithValue("$review", ReviewKey(review));
            header.Parameters.AddWithValue("$x", roi is null ? DBNull.Value : roi.X);
            header.Parameters.AddWithValue("$y", roi is null ? DBNull.Value : roi.Y);
            header.Parameters.AddWithValue("$width", roi is null ? DBNull.Value : roi.Width);
            header.Parameters.AddWithValue("$height", roi is null ? DBNull.Value : roi.Height);
            header.Parameters.AddWithValue("$count", objects.Count);
            header.Parameters.AddWithValue("$now", Time(created));
            await header.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (objects.Count == 0) return;
        await using var insert = Command(connection, transaction, """
            INSERT INTO image_annotation_objects (image_id, revision, position, object_id, class_id, class_variant_id, x, y, width, height)
            VALUES ($id, $revision, $position, $object, $class, $variant, $x, $y, $width, $height);
            """);
        insert.Parameters.AddWithValue("$id", Key(imageId));
        insert.Parameters.AddWithValue("$revision", revision);
        var position = insert.Parameters.Add("$position", SqliteType.Integer);
        var objectId = insert.Parameters.Add("$object", SqliteType.Text);
        var classId = insert.Parameters.Add("$class", SqliteType.Text);
        var variantId = insert.Parameters.Add("$variant", SqliteType.Text);
        var x = insert.Parameters.Add("$x", SqliteType.Real);
        var y = insert.Parameters.Add("$y", SqliteType.Real);
        var width = insert.Parameters.Add("$width", SqliteType.Real);
        var height = insert.Parameters.Add("$height", SqliteType.Real);
        for (var index = 0; index < objects.Count; index++)
        {
            var item = objects[index];
            position.Value = index;
            objectId.Value = Key(item.Id);
            classId.Value = Key(item.ClassId);
            variantId.Value = item.ClassVariantId is { } variant ? Key(variant) : DBNull.Value;
            x.Value = item.Box.X;
            y.Value = item.Box.Y;
            width.Value = item.Box.Width;
            height.Value = item.Box.Height;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Normalized to the oriented frame: finite, inside 0–1 (with a rounding tolerance), positive size. Never clipped.</summary>
    internal static bool IsValidBox(ImageBox? box)
        => box is not null
            && double.IsFinite(box.X) && double.IsFinite(box.Y) && double.IsFinite(box.Width) && double.IsFinite(box.Height)
            && box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0
            && box.X + box.Width <= 1 + BoxTolerance && box.Y + box.Height <= 1 + BoxTolerance;

    // ---- Groups ----

    private sealed record GroupUpdate(string Set, Action<SqliteCommand> Bind);

    private async Task<ImageGroup> ChangeGroupAsync(Guid groupId, long expectedRevision, Func<ImageGroup, GroupUpdate?> change,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        var (group, saved) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindGroupAsync(connection, transaction, groupId, token).ConfigureAwait(false) ?? throw GroupNotFound(groupId);
            if (current.Revision != expectedRevision)
                throw RevisionConflict($"Image group {groupId} has revision {current.Revision}, expected {expectedRevision}.");
            if (change(current) is not { } update) return (current, false);
            await using var command = Command(connection, transaction,
                $"UPDATE image_groups SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id RETURNING {GroupColumns};");
            command.Parameters.AddWithValue("$id", Key(groupId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            update.Bind(command);
            return ((await SingleOrDefaultAsync(command, ReadGroup, token).ConfigureAwait(false))!, true);
        }, cancellationToken).ConfigureAwait(false);
        if (saved) Raise(groups: [new(group.Id, group.Revision)]);
        return group;
    }

    /// <summary>Images and annotations of an archived group or one a generation is filling are read-only.</summary>
    private static void RequireEditable(ImageGroup group)
    {
        if (group.ArchivedAt is not null) throw GroupArchived(group.Id);
        if (group.GenerationState == ImageGenerationState.Producing)
            throw new CoreException("images.group_busy", $"Image group {group.Id} is being filled by a generation.");
    }

    private static void RequireImportable(ImageGroup group)
    {
        if (group.Origin != ImageGroupOrigin.Manual)
            throw new CoreException("images.group_not_manual", $"Image group {group.Id} is generated; only manual groups accept imports.");
        if (group.ArchivedAt is not null) throw GroupArchived(group.Id);
    }

    private static (string Name, string Description) ValidateText(string? name, string? description)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new CoreException("images.name_invalid", $"Group name must contain between 1 and {MaxNameLength} characters.");
        var text = description?.Trim() ?? "";
        if (text.Length > MaxDescriptionLength)
            throw new CoreException("images.description_invalid", $"Group description must not exceed {MaxDescriptionLength} characters.");
        return (trimmed, text);
    }

    // ---- Helpers ----

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("images.project_not_open", $"Project {projectId} is not open; its images are not available.");
    }

    private static void ValidateSelection(IReadOnlyList<ImageRevision> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        ValidateIds(images.Select(item => item?.Id ?? Guid.Empty).ToArray());
    }

    private static void ValidateIds(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0 || ids.Count > MaxSelection || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw SelectionInvalid($"A selection holds 1–{MaxSelection} distinct images.");
    }

    private static string JsonIds(IEnumerable<Guid> ids) => JsonSerializer.Serialize(ids.Select(Key));

    private static string FilterKey(ImageFilter filter)
        => (filter.Archived switch { null => "*", true => "a", false => "n" }) + (filter.ReviewState is { } review ? ReviewKey(review)[0] : '*');

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
        throw new ArgumentException("Image cursor is invalid or belongs to another filter.", nameof(cursor));
    }

    private void Raise(IReadOnlyList<ImagesEntityChange>? groups = null, IReadOnlyList<ImagesEntityChange>? images = null, IReadOnlyList<Guid>? changedIn = null)
        => changed(new ImagesChangedEventArgs(projectId, groups ?? [], images ?? [], changedIn ?? []));

    private static CoreException GroupNotFound(Guid id) => new("images.group_not_found", $"Image group {id} was not found.");

    private static CoreException ImageNotFound(Guid id) => new("images.image_not_found", $"Image {id} was not found.");

    private static CoreException GroupArchived(Guid id) => new("images.group_archived", $"Image group {id} is archived; restore it first.");

    private static CoreException RevisionConflict(string message) => new("images.revision_conflict", message);

    private static CoreException SelectionInvalid(string message) => new("images.selection_invalid", message);

    private static CoreException BoxInvalid(string what) => new("images.box_invalid", $"An {what} box is outside 0–1 or has no size.");
}
