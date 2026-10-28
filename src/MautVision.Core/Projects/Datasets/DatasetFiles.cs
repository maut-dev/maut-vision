using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces.Projects.Datasets;
using MautVision.Core.Interfaces.Projects.Images;
using MautVision.Core.Projects.Storage;

namespace MautVision.Core.Projects.Datasets;

/// <summary>
/// The YOLO folder of a version: files/datasets/&lt;versionId&gt;/images/&lt;split&gt;/&lt;id&gt;.&lt;ext&gt;, labels/&lt;split&gt;/&lt;id&gt;.txt,
/// data.yaml and manifest.json — exactly what training reads. Paths are built only from the version ID, the split and the image
/// ID. File formats: labels with 8 decimals, data.yaml with JSON-quoted names.
/// </summary>
internal static class DatasetFiles
{
    internal const string Kind = DatasetSample.ResourceKind;
    internal const string DataYamlName = "data.yaml";
    internal const string ManifestName = "manifest.json";
    internal static readonly DatasetSplit[] Splits = [DatasetSplit.Train, DatasetSplit.Validation, DatasetSplit.Test];

    internal static string SplitKey(DatasetSplit split) => split switch
    {
        DatasetSplit.Train => "train",
        DatasetSplit.Validation => "val",
        DatasetSplit.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(split), split, "Unknown split."),
    };

    internal static DatasetSplit ParseSplit(string value) => value switch
    {
        "train" => DatasetSplit.Train,
        "val" => DatasetSplit.Validation,
        "test" => DatasetSplit.Test,
        _ => throw new InvalidDataException($"Unknown stored split '{value}'."),
    };

    /// <summary>Extension of a sample file: JPEG stays .jpg (a link to the project file), everything else is PNG.</summary>
    internal static string Extension(ImageFileFormat format) => format == ImageFileFormat.Jpeg ? ".jpg" : ".png";

    internal static string VersionDirectory(MaterialFiles files, Guid versionId) => Path.Combine(files.GetDirectory(Kind), versionId.ToString("D"));

    /// <summary>Path inside the version with '/' separators, as written in manifest.json and the ZIP.</summary>
    internal static string ImagePath(DatasetSplit split, Guid id, ImageFileFormat format) => $"images/{SplitKey(split)}/{id:D}{Extension(format)}";

    internal static string LabelPath(DatasetSplit split, Guid id) => $"labels/{SplitKey(split)}/{id:D}.txt";

    internal static string Full(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates images/ and labels/ with all three split folders; training expects val even when it is empty.</summary>
    internal static void CreateLayout(string root)
    {
        foreach (var folder in new[] { "images", "labels" })
        {
            foreach (var split in Splits) Directory.CreateDirectory(Path.Combine(root, folder, SplitKey(split)));
        }
    }

    /// <summary>"&lt;index&gt; &lt;cx&gt; &lt;cy&gt; &lt;w&gt; &lt;h&gt;\n" per target box with 8 decimals; an empty file for a frame without targets.</summary>
    internal static byte[] Label(IReadOnlyList<DatasetBox> boxes)
    {
        var text = new StringBuilder();
        foreach (var box in boxes)
        {
            text.Append(CultureInfo.InvariantCulture, $"{box.Index} {Fixed(box.CenterX)} {Fixed(box.CenterY)} {Fixed(box.Width)} {Fixed(box.Height)}\n");
        }
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>
    /// data.yaml of the previous exporter, read by the training worker: relative split folders, test only when the version has
    /// test frames, names by index as JSON strings (valid YAML double-quoted scalars).
    /// </summary>
    internal static byte[] DataYaml(IReadOnlyList<DatasetClassEntry> classes, bool hasTest)
    {
        var text = new StringBuilder("path: .\ntrain: images/train\nval: images/val\n");
        if (hasTest) text.Append("test: images/test\n");
        text.Append("names:\n");
        foreach (var entry in classes) text.Append(CultureInfo.InvariantCulture, $"  {entry.Index}: {JsonString(entry.Name)}\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>A string quoted as JavaScript JSON.stringify does: only quotes, backslashes and control characters are escaped.</summary>
    internal static string JsonString(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    var lone = char.IsHighSurrogate(character) ? index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])
                        : char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
                    if (character < 0x20 || lone) text.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    else text.Append(character);
                    break;
            }
        }
        return text.Append('"').ToString();
    }

    /// <summary>The value rounded to 8 decimals like Number.prototype.toFixed(8).</summary>
    internal static string Fixed(double value) => value.ToString("F8", CultureInfo.InvariantCulture);

    /// <summary>SHA-256 of a file as lowercase hex and its length; reads with a large sequential buffer.</summary>
    internal static (string Hash, long Length) HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        return (Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
    }

    internal static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Writes a file of a version that is still being built: nobody reads it before publication and a repeated window rewrites
    /// it, so no temporary name is needed.
    /// </summary>
    internal static void Write(string path, ReadOnlySpan<byte> content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        stream.Write(content);
    }

    /// <summary>Writes a small file through a temporary name and a rename, so a reader never sees half of it.</summary>
    internal static void WriteAtomic(string path, ReadOnlySpan<byte> content)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        {
            stream.Write(content);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Hard link of an immutable project file (a copy when a link is not possible, for example on another volume); an existing
    /// target from an interrupted window is replaced. True — linked.
    /// </summary>
    internal static bool Link(string source, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (HardLink.TryCreate(target, source)) return true;
            // ERROR_ALREADY_EXISTS: a target of an interrupted window; it is replaced.
            if (Marshal.GetLastWin32Error() == 183)
            {
                File.Delete(target);
                if (HardLink.TryCreate(target, source)) return true;
            }
        }
        if (!File.Exists(source)) throw new FileNotFoundException("The source image of a dataset sample is missing.", source);
        File.Copy(source, target, overwrite: true);
        return false;
    }
}
