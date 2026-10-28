using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>Row mapping and stored keys of the backgrounds tables (schema step 5).</summary>
internal static class BackgroundsSql
{
    internal const string SetColumns = "id, name, description, target_width, target_height, created_at, updated_at, archived_at, revision, "
        + "content_revision, item_count, removed_count";

    internal const string ItemColumns = "id, set_id, width, height, byte_size, sha256, pixel_hash, revision, created_at, removed_at, operation_id, "
        + "origin, source_name, source_hash, video_name, timestamp_ms, frame_index, crop_left, crop_top, blur_radius, processor_version, ordinal";

    /// <summary>Column index of the ordinal in <see cref="ItemColumns"/>; read separately for cursors.</summary>
    internal const int OrdinalColumn = 21;

    internal static BackgroundSet ReadSet(SqliteDataReader reader, InputSize projectInput)
    {
        var target = new InputSize(reader.GetInt32(3), reader.GetInt32(4));
        return new BackgroundSet(
            ParseId(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            target,
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            Time(reader.GetInt64(5)),
            Time(reader.GetInt64(6)),
            reader.IsDBNull(7) ? null : Time(reader.GetInt64(7)),
            target == projectInput);
    }

    internal static BackgroundItem ReadItem(SqliteDataReader reader)
    {
        var width = reader.GetInt32(2);
        var height = reader.GetInt32(3);
        return new BackgroundItem(
            ParseId(reader.GetString(0)),
            ParseId(reader.GetString(1)),
            width,
            height,
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            Time(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : Time(reader.GetInt64(9)),
            new BackgroundOrigin(
                ParseOrigin(reader.GetString(11)),
                reader.GetString(10),
                reader.GetString(12),
                reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetInt64(16),
                reader.IsDBNull(17) ? null : new BackgroundCropArea(reader.GetInt32(17), reader.GetInt32(18), width, height),
                reader.IsDBNull(19) ? null : reader.GetDouble(19),
                reader.GetString(20)));
    }

    internal static async Task<BackgroundSet?> FindSetAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id, InputSize projectInput,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {SetColumns} FROM background_sets WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, reader => ReadSet(reader, projectInput), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<BackgroundItem?> FindItemAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {ItemColumns} FROM background_items WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, ReadItem, cancellationToken).ConfigureAwait(false);
    }

    internal static string OriginKey(BackgroundOriginKind value) => value switch
    {
        BackgroundOriginKind.Uploaded => "uploaded",
        BackgroundOriginKind.ImageCrop => "image-crop",
        BackgroundOriginKind.VideoFrame => "video-frame",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown background origin."),
    };

    internal static BackgroundOriginKind ParseOrigin(string value) => value switch
    {
        "uploaded" => BackgroundOriginKind.Uploaded,
        "image-crop" => BackgroundOriginKind.ImageCrop,
        "video-frame" => BackgroundOriginKind.VideoFrame,
        _ => throw new InvalidDataException($"Unknown stored background origin '{value}'."),
    };
}
