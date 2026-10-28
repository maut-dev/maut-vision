using MautVision.Core.Interfaces.Projects.Images;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Images;

/// <summary>Row mapping and stored keys of the images tables (schema step 4).</summary>
internal static class ImagesSql
{
    internal const string GroupColumns = "id, name, description, origin, split_hint, generation_run_id, generation_state, created_at, updated_at, "
        + "archived_at, revision, image_count, archived_image_count";

    internal const string ImageColumns = "id, group_id, display_name, format, width, height, byte_size, sha256, pixel_hash, revision, "
        + "annotation_revision, object_count, review_state, created_at, archived_at, operation_id, lineage_kind, source_family_id, video_name, "
        + "timestamp_ms, frame_index, generation_run_id, sample_index, recipe_revision, ordinal";

    internal static ImageGroup ReadGroup(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        ParseOrigin(reader.GetString(3)),
        ParseSplit(reader.GetString(4)),
        reader.GetInt64(10),
        Time(reader.GetInt64(7)),
        Time(reader.GetInt64(8)),
        reader.IsDBNull(9) ? null : Time(reader.GetInt64(9)),
        reader.IsDBNull(5) ? null : ParseId(reader.GetString(5)),
        reader.IsDBNull(6) ? null : ParseGeneration(reader.GetString(6)),
        reader.GetInt64(11),
        reader.GetInt64(12));

    /// <summary>Reads <see cref="ImageColumns"/>; the ordinal (column 24) is read separately for cursors.</summary>
    internal static ProjectImage ReadImage(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        ParseId(reader.GetString(1)),
        reader.GetString(2),
        ParseFormat(reader.GetString(3)),
        reader.GetInt32(4),
        reader.GetInt32(5),
        reader.GetInt64(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt64(9),
        reader.GetInt64(10),
        reader.GetInt32(11),
        ParseReview(reader.GetString(12)),
        Time(reader.GetInt64(13)),
        reader.IsDBNull(14) ? null : Time(reader.GetInt64(14)),
        reader.GetString(15),
        new ImageLineage(
            ParseLineage(reader.GetString(16)),
            reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetInt64(19),
            reader.IsDBNull(20) ? null : reader.GetInt64(20),
            reader.IsDBNull(21) ? null : ParseId(reader.GetString(21)),
            reader.IsDBNull(22) ? null : reader.GetInt32(22),
            reader.IsDBNull(23) ? null : reader.GetInt64(23)));

    internal static async Task<ImageGroup?> FindGroupAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {GroupColumns} FROM image_groups WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, ReadGroup, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<ProjectImage?> FindImageAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {ImageColumns} FROM images WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, ReadImage, cancellationToken).ConfigureAwait(false);
    }

    internal static string OriginKey(ImageGroupOrigin value) => value switch
    {
        ImageGroupOrigin.Manual => "manual",
        ImageGroupOrigin.Generated => "generated",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown group origin."),
    };

    internal static ImageGroupOrigin ParseOrigin(string value) => value switch
    {
        "manual" => ImageGroupOrigin.Manual,
        "generated" => ImageGroupOrigin.Generated,
        _ => throw new InvalidDataException($"Unknown stored group origin '{value}'."),
    };

    internal static string SplitKey(ImageSplitHint value) => value switch
    {
        ImageSplitHint.Unassigned => "unassigned",
        ImageSplitHint.Train => "train",
        ImageSplitHint.Validation => "validation",
        ImageSplitHint.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown split hint."),
    };

    internal static ImageSplitHint ParseSplit(string value) => value switch
    {
        "unassigned" => ImageSplitHint.Unassigned,
        "train" => ImageSplitHint.Train,
        "validation" => ImageSplitHint.Validation,
        "test" => ImageSplitHint.Test,
        _ => throw new InvalidDataException($"Unknown stored split hint '{value}'."),
    };

    internal static ImageGenerationState ParseGeneration(string value) => value switch
    {
        "producing" => ImageGenerationState.Producing,
        "completed" => ImageGenerationState.Completed,
        "partial" => ImageGenerationState.Partial,
        _ => throw new InvalidDataException($"Unknown stored generation state '{value}'."),
    };

    internal static string ReviewKey(ImageReviewState value) => value switch
    {
        ImageReviewState.Unreviewed => "unreviewed",
        ImageReviewState.Reviewed => "reviewed",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown review state."),
    };

    internal static ImageReviewState ParseReview(string value) => value switch
    {
        "unreviewed" => ImageReviewState.Unreviewed,
        "reviewed" => ImageReviewState.Reviewed,
        _ => throw new InvalidDataException($"Unknown stored review state '{value}'."),
    };

    internal static string FormatKey(ImageFileFormat value) => value switch
    {
        ImageFileFormat.Png => "png",
        ImageFileFormat.Jpeg => "jpeg",
        ImageFileFormat.Webp => "webp",
        ImageFileFormat.Bmp => "bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown image format."),
    };

    internal static ImageFileFormat ParseFormat(string value) => value switch
    {
        "png" => ImageFileFormat.Png,
        "jpeg" => ImageFileFormat.Jpeg,
        "webp" => ImageFileFormat.Webp,
        "bmp" => ImageFileFormat.Bmp,
        _ => throw new InvalidDataException($"Unknown stored image format '{value}'."),
    };

    /// <summary>Extension of the stored file: files/images/&lt;id&gt;&lt;extension&gt;.</summary>
    internal static string Extension(ImageFileFormat value) => value switch
    {
        ImageFileFormat.Png => ".png",
        ImageFileFormat.Jpeg => ".jpg",
        ImageFileFormat.Webp => ".webp",
        ImageFileFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown image format."),
    };

    internal static string LineageKey(ImageLineageKind value) => value switch
    {
        ImageLineageKind.File => "file",
        ImageLineageKind.Video => "video",
        ImageLineageKind.Generated => "generated",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown lineage."),
    };

    internal static ImageLineageKind ParseLineage(string value) => value switch
    {
        "file" => ImageLineageKind.File,
        "video" => ImageLineageKind.Video,
        "generated" => ImageLineageKind.Generated,
        _ => throw new InvalidDataException($"Unknown stored lineage '{value}'."),
    };
}
