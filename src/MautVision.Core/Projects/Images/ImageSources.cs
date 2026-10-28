using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;
using static MautVision.Core.Projects.Images.ImagesSql;

namespace MautVision.Core.Projects.Images;

/// <summary>A group a dataset reads: origin and generation state decide the source family and partial frames, SplitHint the split.</summary>
internal sealed record ImageSourceGroup(Guid Id, string Name, ImageGroupOrigin Origin, ImageSplitHint SplitHint, ImageGenerationState? GenerationState,
    bool Archived);

/// <summary>An object of the current annotation revision.</summary>
internal readonly record struct ImageSourceObject(Guid ClassId, Guid? ClassVariantId, ImageBox Box);

/// <summary>
/// An active image of a selected group with its current annotation: the stored file is files/images/&lt;Id&gt;&lt;extension of
/// Format&gt;, Sha256 its content hash; Objects are empty for unreviewed images (they are skipped before roles matter).
/// </summary>
internal sealed record ImageSource(Guid Id, Guid GroupId, ImageFileFormat Format, int Width, int Height, long ByteSize, string Sha256, string PixelHash,
    long AnnotationRevision, bool Reviewed, string SourceFamilyId, ImageBox? Roi, ImageSourceObject[] Objects);

/// <summary>
/// The selected groups (missing ones are left out) and their active images in group order then import order, read in one
/// transaction, with the revision of exactly what was read (<see cref="ImageSources.ReadRevisionAsync"/>).
/// </summary>
internal sealed record ImageSourceSet(IReadOnlyList<ImageSourceGroup> Groups, IReadOnlyList<ImageSource> Images, string Revision);

/// <summary>
/// Reads of the images tables for dataset plans. The revision changes with every change that matters for a plan: a group's
/// fields, archive and generation state (its revision and state), an import (next_ordinal), a deletion (count) and any change of
/// an image or its annotation (the sums of revisions, which only grow). It is one aggregate per group, so a build can check
/// a plan without reading the images.
/// </summary>
internal static class ImageSources
{
    internal static Task<ImageSourceSet> ReadAsync(ProjectStore store, IReadOnlyList<Guid> groupIds, CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            var groups = await ReadGroupsAsync(connection, transaction, groupIds, token).ConfigureAwait(false);
            var revision = await RevisionAsync(connection, transaction, groupIds, token).ConfigureAwait(false);
            var images = new List<ImageSource>();
            await using var rows = Command(connection, transaction, """
                SELECT i.id, i.format, i.width, i.height, i.byte_size, i.sha256, i.pixel_hash, i.annotation_revision, i.review_state, i.source_family_id,
                    a.roi_x, a.roi_y, a.roi_width, a.roi_height
                FROM images i JOIN image_annotations a ON a.image_id = i.id AND a.revision = i.annotation_revision
                WHERE i.group_id = $group AND i.archived = 0 ORDER BY i.ordinal;
                """);
            var rowGroup = rows.Parameters.Add("$group", SqliteType.Text);
            await using var objects = Command(connection, transaction, """
                SELECT o.image_id, o.class_id, o.class_variant_id, o.x, o.y, o.width, o.height
                FROM images i JOIN image_annotation_objects o ON o.image_id = i.id AND o.revision = i.annotation_revision
                WHERE i.group_id = $group AND i.archived = 0 AND i.review_state = 'reviewed' AND i.object_count > 0
                ORDER BY i.ordinal, o.position;
                """);
            var objectGroup = objects.Parameters.Add("$group", SqliteType.Text);
            foreach (var group in groups)
            {
                objectGroup.Value = Key(group.Id);
                var byImage = new Dictionary<string, List<ImageSourceObject>>(StringComparer.Ordinal);
                await using (var reader = await objects.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    string? current = null;
                    List<ImageSourceObject>? list = null;
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var imageId = reader.GetString(0);
                        if (imageId != current)
                        {
                            current = imageId;
                            list = [];
                            byImage[imageId] = list;
                        }
                        list!.Add(new ImageSourceObject(ParseId(reader.GetString(1)), reader.IsDBNull(2) ? null : ParseId(reader.GetString(2)),
                            new ImageBox(reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6))));
                    }
                }
                rowGroup.Value = Key(group.Id);
                await using var imageReader = await rows.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await imageReader.ReadAsync(token).ConfigureAwait(false))
                {
                    var id = imageReader.GetString(0);
                    var reviewed = imageReader.GetString(8) == "reviewed";
                    ImageBox? roi = imageReader.IsDBNull(10) ? null
                        : new ImageBox(imageReader.GetDouble(10), imageReader.GetDouble(11), imageReader.GetDouble(12), imageReader.GetDouble(13));
                    images.Add(new ImageSource(ParseId(id), group.Id, ImagesSql.ParseFormat(imageReader.GetString(1)), imageReader.GetInt32(2), imageReader.GetInt32(3),
                        imageReader.GetInt64(4), imageReader.GetString(5), imageReader.GetString(6), imageReader.GetInt64(7), reviewed, imageReader.GetString(9),
                        roi, reviewed && byImage.TryGetValue(id, out var found) ? [.. found] : []));
                }
            }
            return new ImageSourceSet(groups, images, revision);
        }, cancellationToken);

    /// <summary>The revision <see cref="ReadAsync"/> would report for these groups now; aggregates only, no image rows are returned.</summary>
    internal static Task<string> ReadRevisionAsync(ProjectStore store, IReadOnlyList<Guid> groupIds, CancellationToken cancellationToken)
        => store.ReadAsync(async (connection, token) =>
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            return await RevisionAsync(connection, transaction, groupIds, token).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<List<ImageSourceGroup>> ReadGroupsAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> groupIds,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, """
            SELECT id, name, origin, split_hint, generation_state, archived_at IS NOT NULL FROM image_groups WHERE id = $id;
            """);
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var result = new List<ImageSourceGroup>(groupIds.Count);
        foreach (var groupId in groupIds)
        {
            id.Value = Key(groupId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) continue;
            result.Add(new ImageSourceGroup(groupId, reader.GetString(1), ParseOrigin(reader.GetString(2)), ParseSplit(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseGeneration(reader.GetString(4)), reader.GetInt64(5) == 1));
        }
        return result;
    }

    private static async Task<string> RevisionAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<Guid> groupIds,
        CancellationToken cancellationToken)
    {
        await using var group = Command(connection, transaction, """
            SELECT revision, next_ordinal, image_count, archived_image_count, coalesce(generation_state, ''), archived_at IS NOT NULL
            FROM image_groups WHERE id = $id;
            """);
        var groupId = group.Parameters.Add("$id", SqliteType.Text);
        await using var images = Command(connection, transaction,
            "SELECT count(*), coalesce(sum(revision), 0), coalesce(sum(annotation_revision), 0) FROM images WHERE group_id = $id;");
        var imagesGroup = images.Parameters.Add("$id", SqliteType.Text);
        var text = new StringBuilder();
        foreach (var id in groupIds)
        {
            groupId.Value = imagesGroup.Value = Key(id);
            text.Append(Key(id)).Append(':');
            await using (var reader = await group.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    text.Append("missing\n");
                    continue;
                }
                text.Append(CultureInfo.InvariantCulture, $"{reader.GetInt64(0)}:{reader.GetInt64(1)}:{reader.GetInt64(2)}:{reader.GetInt64(3)}:{reader.GetString(4)}:{reader.GetInt64(5)}");
            }
            await using (var reader = await images.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                text.Append(CultureInfo.InvariantCulture, $":{reader.GetInt64(0)}:{reader.GetInt64(1)}:{reader.GetInt64(2)}\n");
            }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

}
