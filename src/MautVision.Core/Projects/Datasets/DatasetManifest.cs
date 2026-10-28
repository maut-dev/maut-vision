using System.Text.Encodings.Web;
using System.Text.Json;
using MautVision.Core.Interfaces.Projects.Datasets;

namespace MautVision.Core.Projects.Datasets;

/// <summary>
/// manifest.json of a version: everything training and an export need to trust the folder — the frame size, the specification,
/// the classes by index (training reads <c>classes</c>), the split counts and every sample with its files, hashes and geometry.
/// Written as a stream (a large version gives tens of megabytes) through a temporary file and a rename.
/// </summary>
internal static class DatasetManifest
{
    internal const int SchemaVersion = 1;

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    internal static void Write(string path, DatasetVersion version, IReadOnlyList<SampleRow> samples)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("datasetId", version.Id.ToString("D"));
            writer.WriteString("name", version.Name);
            writer.WriteNumber("width", version.Width);
            writer.WriteNumber("height", version.Height);
            writer.WritePropertyName("specification");
            JsonSerializer.Serialize(writer, version.Specification, DatasetsSql.Json);
            writer.WriteStartArray("classes");
            foreach (var entry in version.ClassMap)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", entry.Index);
                writer.WriteString("classId", entry.ClassId.ToString("D"));
                writer.WriteString("name", entry.Name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("counts");
            writer.WriteNumber("train", version.Counts.Train);
            writer.WriteNumber("val", version.Counts.Validation);
            writer.WriteNumber("test", version.Counts.Test);
            writer.WriteEndObject();
            writer.WriteStartArray("samples");
            foreach (var sample in samples)
            {
                var format = sample.Format ?? throw new InvalidOperationException($"Sample {sample.ImageId} has no file yet.");
                writer.WriteStartObject();
                writer.WriteString("id", sample.ImageId.ToString("D"));
                writer.WriteString("split", DatasetFiles.SplitKey(sample.Split));
                writer.WriteString("image", DatasetFiles.ImagePath(sample.Split, sample.ImageId, format));
                writer.WriteString("label", DatasetFiles.LabelPath(sample.Split, sample.ImageId));
                writer.WriteString("imageHash", sample.ImageHash);
                writer.WriteString("labelHash", sample.LabelHash);
                writer.WriteString("pixelHash", sample.PixelHash);
                writer.WriteNumber("byteSize", sample.ByteSize ?? 0);
                writer.WriteNumber("width", sample.Width);
                writer.WriteNumber("height", sample.Height);
                writer.WriteNumber("annotationRevision", sample.AnnotationRevision);
                writer.WriteString("groupId", sample.GroupId.ToString("D"));
                writer.WritePropertyName("roi");
                if (sample.Roi is { } roi) JsonSerializer.Serialize(writer, roi, DatasetsSql.Json);
                else writer.WriteNullValue();
                writer.WritePropertyName("boxes");
                JsonSerializer.Serialize(writer, sample.Boxes, DatasetsSql.Json);
                writer.WritePropertyName("negatives");
                JsonSerializer.Serialize(writer, sample.Negatives, DatasetsSql.Json);
                writer.WriteEndObject();
                if (writer.BytesPending > 1 << 16) writer.Flush();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>dataset-metadata.json of an export, as the previous exporter wrote it, with the frame size and the build time.</summary>
    internal static byte[] Metadata(DatasetVersion version)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("id", version.Id.ToString("D"));
            writer.WriteString("name", version.Name);
            writer.WriteString("description", version.Description);
            writer.WriteString("manifestHash", version.ManifestHash);
            writer.WriteNumber("width", version.Width);
            writer.WriteNumber("height", version.Height);
            writer.WriteString("createdAt", version.CreatedAt);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>negatives/&lt;split&gt;/&lt;id&gt;.json of an export: the negatives of a sample.</summary>
    internal static byte[] Negatives(IReadOnlyList<DatasetNegative> negatives) => JsonSerializer.SerializeToUtf8Bytes(negatives, DatasetsSql.Json);

}
