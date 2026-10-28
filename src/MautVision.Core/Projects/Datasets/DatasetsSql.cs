using System.Text.Json;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Images;
using Microsoft.Data.Sqlite;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Datasets;

/// <summary>A version row with its storage state (building, ready, deleted) and the hash of its data.yaml.</summary>
internal sealed record VersionRow(DatasetVersion Version, string State, string DataYamlHash);

/// <summary>
/// A sample row as the build and the operations use it: position is the build order; Format, ByteSize, ImageHash and LabelHash
/// are null until the build produced the files.
/// </summary>
internal sealed record SampleRow(
    Guid VersionId,
    DatasetSplit Split,
    Guid ImageId,
    long Position,
    Guid ResourceId,
    Guid GroupId,
    bool Generated,
    ImageFileFormat SourceFormat,
    string SourceSha256,
    ImageFileFormat? Format,
    int Width,
    int Height,
    string PixelHash,
    long AnnotationRevision,
    ImageBox? Roi,
    DatasetBox[] Boxes,
    DatasetNegative[] Negatives,
    long? ByteSize,
    string? ImageHash,
    string? LabelHash)
{
    internal DatasetSample ToSample() => new(ImageId, Split, Format ?? ImageFileFormat.Png, Width, Height, ByteSize ?? 0, ImageHash ?? "", LabelHash ?? "", PixelHash,
        AnnotationRevision, Roi, Boxes, Negatives, GroupId, ResourceId);
}

/// <summary>Row mapping and stored keys of the dataset tables (schema step 7).</summary>
internal static class DatasetsSql
{
    internal static readonly JsonSerializerOptions Json = JobsManager.Json;

    internal const string VersionColumns = "id, state, plan_id, name, description, revision, archived_at, integrity_state, verified_at, width, height, "
        + "specification, class_map, variants, composition, manifest_hash, data_yaml_hash, created_at, updated_at, deleted_at";

    internal const string SampleColumns = "version_id, split, image_id, position, resource_id, group_id, generated, source_format, source_sha256, format, "
        + "width, height, pixel_hash, annotation_revision, roi_x, roi_y, roi_width, roi_height, boxes, negatives, byte_size, image_hash, label_hash";

    internal const string PlanColumns = "id, specification, width, height, classes, composition, skipped, warnings, sufficient, hash, source_revision, created_at";

    internal static VersionRow ReadVersion(SqliteDataReader reader) => new(new DatasetVersion(
        ParseId(reader.GetString(0)),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.IsDBNull(6) ? null : Time(reader.GetInt64(6)),
        ParseIntegrity(reader.GetString(7)),
        reader.IsDBNull(8) ? null : Time(reader.GetInt64(8)),
        reader.GetInt32(9),
        reader.GetInt32(10),
        Deserialize<DatasetSpecification>(reader.GetString(11)),
        Deserialize<DatasetClassEntry[]>(reader.GetString(12)),
        Deserialize<DatasetVariantEntry[]>(reader.GetString(13)),
        Deserialize<DatasetComposition>(reader.GetString(14)),
        reader.GetString(15),
        ParseId(reader.GetString(2)),
        Time(reader.GetInt64(17)),
        Time(reader.GetInt64(18)),
        reader.IsDBNull(19) ? null : Time(reader.GetInt64(19))), reader.GetString(1), reader.GetString(16));

    internal static SampleRow ReadSample(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        DatasetFiles.ParseSplit(reader.GetString(1)),
        ParseId(reader.GetString(2)),
        reader.GetInt64(3),
        ParseId(reader.GetString(4)),
        ParseId(reader.GetString(5)),
        reader.GetInt64(6) == 1,
        ImagesSql.ParseFormat(reader.GetString(7)),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : ImagesSql.ParseFormat(reader.GetString(9)),
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetString(12),
        reader.GetInt64(13),
        reader.IsDBNull(14) ? null : new ImageBox(reader.GetDouble(14), reader.GetDouble(15), reader.GetDouble(16), reader.GetDouble(17)),
        Deserialize<DatasetBox[]>(reader.GetString(18)),
        Deserialize<DatasetNegative[]>(reader.GetString(19)),
        reader.IsDBNull(20) ? null : reader.GetInt64(20),
        reader.IsDBNull(21) ? null : reader.GetString(21),
        reader.IsDBNull(22) ? null : reader.GetString(22));

    /// <summary>A plan row; SourceRevision is what the build compares with the sources now.</summary>
    internal static (DatasetPlan Plan, string SourceRevision) ReadPlan(SqliteDataReader reader) => (new DatasetPlan(
        ParseId(reader.GetString(0)),
        Deserialize<DatasetSpecification>(reader.GetString(1)),
        reader.GetInt32(2),
        reader.GetInt32(3),
        Deserialize<DatasetClassEntry[]>(reader.GetString(4)),
        Deserialize<DatasetComposition>(reader.GetString(5)),
        Deserialize<DatasetPlanSkips>(reader.GetString(6)),
        Deserialize<DatasetPlanWarning[]>(reader.GetString(7)),
        reader.GetInt64(8) == 1,
        reader.GetString(9),
        Time(reader.GetInt64(11))), reader.GetString(10));

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    internal static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException($"Stored {typeof(T).Name} is empty.");

    internal static string IntegrityKey(DatasetIntegrityState value) => value switch
    {
        DatasetIntegrityState.Unverified => "unverified",
        DatasetIntegrityState.Verified => "verified",
        DatasetIntegrityState.Corrupt => "corrupt",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown integrity state."),
    };

    internal static DatasetIntegrityState ParseIntegrity(string value) => value switch
    {
        "unverified" => DatasetIntegrityState.Unverified,
        "verified" => DatasetIntegrityState.Verified,
        "corrupt" => DatasetIntegrityState.Corrupt,
        _ => throw new InvalidDataException($"Unknown stored integrity state '{value}'."),
    };

    /// <summary>ID of a sample in the resource channel, derived from the version and the image so that every version has its own.</summary>
    internal static Guid ResourceId(Guid versionId, Guid imageId)
    {
        Span<byte> seed = stackalloc byte[32];
        versionId.TryWriteBytes(seed);
        imageId.TryWriteBytes(seed[16..]);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(seed, hash);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }
}
