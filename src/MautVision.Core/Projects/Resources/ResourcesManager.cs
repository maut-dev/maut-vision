using System.Collections.Concurrent;
using System.Globalization;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Resources;
using MautVision.Core.Projects;
using MautVision.Core.Projects.Storage;
using MautVision.Core.Resources;

namespace MautVision.Core.Projects.Resources;

/// <summary>
/// Opens material files of one project. Thumbnails live in projects/&lt;id&gt;/cache/thumbnails/v1/&lt;kind&gt;/&lt;2 hex&gt;/
/// under a name made of the material ID and the size and write time of its original: a changed original gets a new
/// thumbnail and the outdated one is removed. Deleting the cache directory is always safe.
/// </summary>
internal sealed class ResourcesManager(ProjectStore store, ResourceKinds kinds, ThumbnailGenerator thumbnails) : IResourcesManager
{
    internal const string CacheDirectoryName = "cache";
    // A new version directory replaces thumbnails made with other parameters; old ones go with the cache.
    internal static readonly string[] ThumbnailPath = ["thumbnails", "v1"];

    private const FileShare ReadShare = FileShare.Read | FileShare.Delete;
    private readonly ConcurrentDictionary<string, bool> validatedDirectories = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ResourceContent> OpenAsync(ResourceReference reference, ResourceVariant variant, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(variant))
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        var resolve = kinds.Find(reference.Kind);
        var resolveNested = resolve is null ? kinds.FindNested(reference.Kind) : null;
        if (resolve is null && resolveNested is null)
        {
            throw new CoreException("resources.kind_unknown", $"Resource kind {Truncate(reference.Kind)} is not registered.");
        }
        if (reference.Id == Guid.Empty)
        {
            throw NotFound(reference);
        }
        var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        string path;
        if (resolve is not null)
        {
            var extension = await resolve(store, files, reference.Id, cancellationToken).ConfigureAwait(false) ?? throw NotFound(reference);
            path = files.GetPath(reference.Kind, reference.Id, extension);
        }
        else
        {
            var relative = await resolveNested!(store, files, reference.Id, cancellationToken).ConfigureAwait(false) ?? throw NotFound(reference);
            path = await Task.Run(() => NestedPath(files, reference, relative), cancellationToken).ConfigureAwait(false);
        }
        var contentType = ResourceContentTypes.Find(Path.GetExtension(path)) ?? throw NotFound(reference);
        if (variant == ResourceVariant.Original)
        {
            return await Task.Run(() => OpenOriginal(files, reference, path, contentType), cancellationToken).ConfigureAwait(false);
        }
        if (!ResourceContentTypes.IsImage(contentType))
        {
            throw new CoreException("resources.thumbnail_unsupported", $"Resource {reference.Kind}/{reference.Id} is not an image.");
        }
        return await OpenThumbnailAsync(files, reference, path, cancellationToken).ConfigureAwait(false);
    }

    private static ResourceContent OpenOriginal(MaterialFiles files, ResourceReference reference, string path, string contentType)
    {
        RequireMaterialFile(files, reference, path);
        var stream = OpenRead(path) ?? throw NotFound(reference);
        return new ResourceContent(stream, contentType, File.GetLastWriteTimeUtc(stream.SafeFileHandle));
    }

    private async Task<ResourceContent> OpenThumbnailAsync(MaterialFiles files, ResourceReference reference, string path, CancellationToken cancellationToken)
    {
        var (directory, name) = await Task.Run(() =>
        {
            var original = RequireMaterialFile(files, reference, path);
            var id = reference.Id.ToString("D");
            var key = string.Create(CultureInfo.InvariantCulture, $"{id}-{original.Length:x}-{original.LastWriteTimeUtc.Ticks:x}.jpg");
            return (ThumbnailDirectory(reference), key);
        }, cancellationToken).ConfigureAwait(false);
        var destination = Path.Combine(directory, name);
        // Two attempts: the cache may be deleted between generation and opening.
        for (var attempt = 0; ; attempt++)
        {
            var cached = await Task.Run(() => OpenCached(directory, destination), cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
            if (attempt == 1)
            {
                throw new IOException("Thumbnail cache file disappeared after generation.");
            }
            await Task.Run(() => EnsureCacheDirectory(directory, force: true), cancellationToken).ConfigureAwait(false);
            try
            {
                await thumbnails.EnsureAsync(path, destination, reference.Id.ToString("D") + "-*.jpg", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw NotFound(reference);
            }
        }
    }

    private ResourceContent? OpenCached(string directory, string destination)
    {
        EnsureCacheDirectory(directory, force: false);
        var stream = OpenRead(destination);
        return stream is null ? null : new ResourceContent(stream, "image/jpeg", File.GetLastWriteTimeUtc(stream.SafeFileHandle));
    }

    private string ThumbnailDirectory(ResourceReference reference) => ThumbnailDirectory(reference.Kind, reference.Id);

    private string ThumbnailDirectory(string kind, Guid id)
        => Path.Combine([store.ProjectDirectory, CacheDirectoryName, .. ThumbnailPath, kind, id.ToString("N")[..2]]);

    /// <summary>
    /// Removes the cached thumbnails of a material whose original was deleted. Best effort: a leftover is never served
    /// (resources resolve only existing rows) and goes with the cache, so problems are swallowed.
    /// </summary>
    internal void EvictThumbnails(string kind, Guid id)
    {
        var directory = ThumbnailDirectory(kind, id);
        try
        {
            // A link placed in the cache must not redirect deletion outside the project: every level is checked as for writes.
            if (!validatedDirectories.ContainsKey(directory) && !IsCacheDirectory(directory))
            {
                return;
            }
            foreach (var file in Directory.EnumerateFiles(directory, id.ToString("D") + "-*.jpg"))
            {
                File.Delete(file);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // DirectoryNotFoundException included: nothing was cached for this material.
        }
    }

    /// <summary>True when every level below the project directory exists and is a real directory; nothing is created.</summary>
    private bool IsCacheDirectory(string directory)
    {
        var current = store.ProjectDirectory;
        foreach (var segment in Path.GetRelativePath(current, directory).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (ProjectStore.ValidateEntry(current) is not { } attributes || (attributes & FileAttributes.Directory) == 0)
            {
                return false;
            }
        }
        validatedDirectories[directory] = true;
        return true;
    }

    /// <summary>
    /// Creates and checks each level below the project directory, so a link placed in the cache cannot redirect reads or
    /// writes outside the project. A checked directory is remembered; force repeats the check after a cache miss.
    /// </summary>
    private void EnsureCacheDirectory(string directory, bool force)
    {
        if (!force && validatedDirectories.ContainsKey(directory))
        {
            return;
        }
        var current = store.ProjectDirectory;
        var relative = Path.GetRelativePath(current, directory);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (ProjectStore.ValidateEntry(current) is { } attributes)
            {
                ProjectsManager.RequireDirectory(attributes);
                continue;
            }
            Directory.CreateDirectory(current);
            ProjectsManager.RequireDirectory(ProjectStore.ValidateEntry(current)!.Value);
        }
        validatedDirectories[directory] = true;
    }

    /// <summary>
    /// The file of a nested material: every folder between the kind directory and the file must be a real directory, so a link
    /// placed inside the version cannot redirect a read; segments come from the material's row, never from the request.
    /// </summary>
    private static string NestedPath(MaterialFiles files, ResourceReference reference, string relative)
    {
        var segments = relative.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw NotFound(reference);
        }
        var current = files.GetDirectory(reference.Kind);
        foreach (var segment in segments[..^1])
        {
            current = Path.Combine(current, segment);
            if (ProjectStore.ValidateEntry(current) is not { } attributes || (attributes & FileAttributes.Directory) == 0) throw NotFound(reference);
        }
        return Path.Combine(current, segments[^1]);
    }

    /// <summary>The kind directory may appear after the store was opened, so it and the file are checked on every request.</summary>
    private static FileInfo RequireMaterialFile(MaterialFiles files, ResourceReference reference, string path)
    {
        if (ProjectStore.ValidateEntry(files.GetDirectory(reference.Kind)) is not { } directory || (directory & FileAttributes.Directory) == 0
            || ProjectStore.ValidateEntry(path) is not { } file || (file & FileAttributes.Directory) != 0)
        {
            throw NotFound(reference);
        }
        return new FileInfo(path);
    }

    private static FileStream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, ReadShare, 1, FileOptions.Asynchronous);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static CoreException NotFound(ResourceReference reference)
        => new("resources.not_found", $"Resource {reference.Kind}/{reference.Id} was not found.");

    private static string Truncate(string? value) => value is null ? "<null>" : value.Length <= 64 ? value : value[..64] + "…";
}
