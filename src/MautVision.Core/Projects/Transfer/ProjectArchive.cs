using System.Text.Json;
using System.Text.Json.Serialization;
using MautVision.Core.Interfaces;

namespace MautVision.Core.Projects.Transfer;

/// <summary>A path of the archive stored once in the archive and given again under another name (a hard link of the project).</summary>
internal sealed record ArchiveLink(string Path, string Target);

/// <summary>
/// manifest.json of a sharp-app project archive. Format and FormatVersion identify the layout; Links lists the paths whose content is
/// the content of another entry (hard links in the project: dataset frames link the project images).
/// </summary>
internal sealed record ArchiveManifest
{
    public required string Format { get; init; }
    public required int FormatVersion { get; init; }
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required long Files { get; init; }
    public required long Bytes { get; init; }
    public required IReadOnlyList<ArchiveLink> Links { get; init; }
}

/// <summary>
/// The layout of a project archive: manifest.json, project.json and a snapshot of project.db (deflate), files/** (stored: images and
/// weights do not shrink). Left out: cache/, files/generation-runs/, the working folders of training and checks, temporary names
/// that start with a dot, project.db-wal/-shm (the snapshot is complete without them).
/// </summary>
internal static class ProjectArchive
{
    internal const string Format = "maut-vision-sharp";
    internal const int FormatVersion = 1;
    internal const string ManifestName = "manifest.json";
    internal const string MetadataName = "project.json";
    internal const string DatabaseName = "project.db";
    internal const string FilesPrefix = "files/";
    internal const int MaxManifestBytes = 256 * 1024 * 1024;
    private const int MaxPathLength = 400;
    private const int MaxSegmentLength = 200;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Directories under files/ that are working data of runs, not project data.</summary>
    internal static bool SkipDirectory(string relativePath) => relativePath == "files/generation-runs";

    /// <summary>
    /// Whether an entry name of an archive may be extracted: the three root entries, or files/ followed by plain segments (no dot names,
    /// no '..', no drive or separator characters).
    /// </summary>
    internal static bool IsAllowedName(string name)
    {
        if (name is ManifestName or MetadataName or DatabaseName) return true;
        if (!name.StartsWith(FilesPrefix, StringComparison.Ordinal) || name.Length > MaxPathLength) return false;
        var segments = name.Split('/');
        if (segments.Length < 3) return false;
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length is 0 or > MaxSegmentLength || segment.StartsWith('.') || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || segment.Any(char.IsControl) || IsReservedDeviceName(segment))
                return false;
        }
        return !SkipDirectory(segments[0] + "/" + segments[1]);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3]));
    }

    /// <summary>
    /// Reads a manifest. A manifest of an unsupported early format (with tables) — <c>projects.archive_legacy</c>; another format, a newer
    /// format version or a malformed manifest — <c>projects.archive_unknown</c>.
    /// </summary>
    internal static ArchiveManifest ReadManifest(byte[] content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException error)
        {
            throw new CoreException("projects.archive_unknown", "manifest.json is not JSON.", error);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Unknown("manifest.json is not an object.");
            if (root.TryGetProperty("tables", out _))
                throw new CoreException("projects.archive_legacy", "The archive has an unsupported early format (manifest with tables).");
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw Unknown("The archive has another format.");
            if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number != FormatVersion)
                throw Unknown($"Archive format version {version} is not supported.");
            try
            {
                return root.Deserialize<ArchiveManifest>(Json) ?? throw Unknown("manifest.json is empty.");
            }
            catch (JsonException error)
            {
                throw new CoreException("projects.archive_unknown", "manifest.json has invalid fields.", error);
            }
        }
    }

    internal static byte[] WriteManifest(ArchiveManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Json);

    private static CoreException Unknown(string message) => new("projects.archive_unknown", message);
}
