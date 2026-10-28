using System.Globalization;
using System.Text;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Catalog;
using Microsoft.Data.Sqlite;

namespace MautVision.Core.Projects.Catalog;

/// <summary>Shared SQL pieces of the catalog tables (schema step 3): row mapping, counters of catalog_state, names.</summary>
internal static class CatalogSql
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 2000;

    internal const string VariantColumns = "id, name, description, sort_order, created_at, updated_at, archived_at, revision";
    internal const string ClassColumns = "id, name, description, sort_order, created_at, updated_at, archived_at, revision";

    /// <summary>Binding columns with availability; the query must join classes c and variants v.</summary>
    internal const string ClassVariantColumns = "b.id, b.class_id, b.variant_id, b.color, b.created_at, b.updated_at, b.archived_at, b.revision, "
        + "(b.archived_at IS NULL AND c.archived_at IS NULL AND v.archived_at IS NULL), "
        + "b.asset_count, b.opaque_asset_count, b.max_asset_width, b.max_asset_height";

    internal const string ClassVariantFrom = "class_variants b JOIN classes c ON c.id = b.class_id JOIN variants v ON v.id = b.variant_id";

    internal const string AssetColumns = "id, class_variant_id, format, width, height, has_alpha, byte_size, sha256, created_at, operation_id";

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    internal static string Key(Guid id) => id.ToString("D");

    internal static Guid ParseId(string value) => Guid.ParseExact(value, "D");

    internal static long Time(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    internal static DateTimeOffset Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);

    // Stored times have millisecond precision; returned records use the same so that memory and project.db agree.
    internal static DateTimeOffset Now() => Time(Time(DateTimeOffset.UtcNow));

    /// <summary>
    /// Trims and checks a name and a description; returns them with the uniqueness key. The key folds case with the
    /// invariant culture after NFC normalization, which matches the old toLocaleLowerCase for Latin and Cyrillic.
    /// </summary>
    internal static (string Name, string Description, string Key) ValidateText(string area, string? name, string? description)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
            throw new CoreException($"{area}.name_invalid", $"Name must contain between 1 and {MaxNameLength} characters.");
        var text = description?.Trim() ?? "";
        if (text.Length > MaxDescriptionLength)
            throw new CoreException($"{area}.description_invalid", $"Description must not exceed {MaxDescriptionLength} characters.");
        return (trimmed, text, NameKey(trimmed));
    }

    internal static string NameKey(string name) => name.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    internal static bool IsUniqueViolation(SqliteException error) => error.SqliteErrorCode == 19 && error.SqliteExtendedErrorCode is 2067 or 1555;

    internal static ProjectVariant ReadVariant(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        Time(reader.GetInt64(4)),
        Time(reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : Time(reader.GetInt64(6)),
        reader.GetInt64(7));

    internal static CatalogClass ReadClass(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        Time(reader.GetInt64(4)),
        Time(reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : Time(reader.GetInt64(6)),
        reader.GetInt64(7));

    internal static ClassVariant ReadClassVariant(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        ParseId(reader.GetString(1)),
        ParseId(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        Time(reader.GetInt64(4)),
        Time(reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : Time(reader.GetInt64(6)),
        reader.GetInt64(7),
        reader.GetInt64(8) == 1,
        reader.GetInt64(9),
        reader.GetInt64(10),
        reader.GetInt32(11),
        reader.GetInt32(12));

    internal static ClassAsset ReadAsset(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        ParseId(reader.GetString(1)),
        ParseFormat(reader.GetString(2)),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetInt64(5) == 1,
        reader.GetInt64(6),
        reader.GetString(7),
        Time(reader.GetInt64(8)),
        reader.GetString(9));

    internal static string FormatKey(ClassAssetFormat format) => format switch
    {
        ClassAssetFormat.Png => "png",
        ClassAssetFormat.Jpeg => "jpeg",
        ClassAssetFormat.Webp => "webp",
        ClassAssetFormat.Bmp => "bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown asset format."),
    };

    internal static ClassAssetFormat ParseFormat(string value) => value switch
    {
        "png" => ClassAssetFormat.Png,
        "jpeg" => ClassAssetFormat.Jpeg,
        "webp" => ClassAssetFormat.Webp,
        "bmp" => ClassAssetFormat.Bmp,
        _ => throw new InvalidDataException($"Unknown stored asset format '{value}'."),
    };

    /// <summary>Extension of the stored file: files/class-assets/&lt;id&gt;&lt;extension&gt;.</summary>
    internal static string Extension(ClassAssetFormat format) => format switch
    {
        ClassAssetFormat.Png => ".png",
        ClassAssetFormat.Jpeg => ".jpg",
        ClassAssetFormat.Webp => ".webp",
        ClassAssetFormat.Bmp => ".bmp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown asset format."),
    };

    /// <summary>Counters of the catalog; every catalog write bumps the catalog revision, an order change also the order revision.</summary>
    internal static async Task<(long OrderRevision, long CatalogRevision)> BumpAsync(SqliteConnection connection, SqliteTransaction transaction,
        bool order, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, order
            ? "UPDATE catalog_state SET class_order_revision = class_order_revision + 1, catalog_revision = catalog_revision + 1 WHERE id = 1 RETURNING class_order_revision, catalog_revision;"
            : "UPDATE catalog_state SET catalog_revision = catalog_revision + 1 WHERE id = 1 RETURNING class_order_revision, catalog_revision;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Catalog state row is missing.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    internal static async Task<(long OrderRevision, long CatalogRevision)> ReadStateAsync(SqliteConnection connection, SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT class_order_revision, catalog_revision FROM catalog_state WHERE id = 1;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Catalog state row is missing.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    internal static async Task<long> ScalarAsync(SqliteCommand command, CancellationToken cancellationToken)
        => Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture);

    internal static async Task<T?> SingleOrDefaultAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
        where T : class
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? read(reader) : null;
    }

    internal static async Task<List<T>> ListAsync<T>(SqliteCommand command, Func<SqliteDataReader, T> read, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(read(reader));
        return result;
    }

    internal static async Task<ClassVariant?> FindClassVariantAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {ClassVariantColumns} FROM {ClassVariantFrom} WHERE b.id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, ReadClassVariant, cancellationToken).ConfigureAwait(false);
    }
}
