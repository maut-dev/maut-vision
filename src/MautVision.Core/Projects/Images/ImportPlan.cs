using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects.Images;

namespace MautVision.Core.Projects.Images;

/// <summary>
/// One file of an import: its absolute path and the name shown to the user (file name, or path relative to the folder). A
/// frame of a video carries the index of its source and its place in the source's frame range.
/// </summary>
internal sealed record ImportItem(string Path, string Name, ImportFrame? Frame = null);

/// <summary>Frame <paramref name="Index"/> of the video source <paramref name="Source"/>, at <paramref name="TimestampMs"/>.</summary>
internal sealed record ImportFrame(int Source, int Index, long TimestampMs, string VideoName);

/// <summary>
/// Expands the sources of an import into the ordered list of files: files in the given order, a folder's files with the
/// accepted extensions sorted by relative path in natural order (frame2 before frame10). Links and reparse points inside
/// a folder, hidden and system entries are not read. Digest identifies the list, so a resumed job can tell that the
/// folders changed meanwhile. A video gives one item per timestamp of its range; its digest part covers the size and the
/// write time of the file, so a video replaced before a resumed run is detected the same way.
/// </summary>
internal static class ImportPlan
{
    internal static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private const int MaxNameLength = ImagesManager.MaxDisplayNameLength;
    private const int VideoNameLength = 160;

    internal static (IReadOnlyList<ImportItem> Items, string Digest) Expand(IReadOnlyList<ImageImportSource> sources, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var items = new List<ImportItem>();
        var digest = new StringBuilder();
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(source);
            var first = items.Count;
            switch (source.Kind)
            {
                case ImageImportSourceKind.File:
                    items.Add(new ImportItem(source.Path, Shorten(Path.GetFileName(source.Path))));
                    break;
                case ImageImportSourceKind.Folder:
                    items.AddRange(ExpandFolder(source.Path, source.Recursive, cancellationToken));
                    break;
                case ImageImportSourceKind.Video:
                    digest.Append(VideoStamp(source)).Append('\n');
                    items.AddRange(ExpandVideo(index, source));
                    continue;
                default:
                    throw new ArgumentOutOfRangeException(nameof(sources), source.Kind, "Unknown import source kind.");
            }
            for (var item = first; item < items.Count; item++) digest.Append(items[item].Path).Append('\n');
        }
        return (items, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString()))));
    }

    /// <summary>Frames named «&lt;video name, up to 160&gt;-frame-&lt;timestamp&gt;.png».</summary>
    private static IEnumerable<ImportItem> ExpandVideo(int index, ImageImportSource source)
    {
        var range = source.Frames!;
        var videoName = Shorten(Path.GetFileName(source.Path));
        var prefix = videoName.Length <= VideoNameLength ? videoName : videoName[..VideoNameLength];
        for (var frame = 0; frame < range.Count; frame++)
        {
            var timestamp = range.TimestampMs(frame);
            yield return new ImportItem(source.Path, prefix + "-frame-" + timestamp.ToString(CultureInfo.InvariantCulture) + ".png",
                new ImportFrame(index, frame, timestamp, videoName));
        }
    }

    /// <summary>The digest part of a video: path, size, write time and the frame range.</summary>
    private static string VideoStamp(ImageImportSource source)
    {
        var range = source.Frames ?? throw new ArgumentException("A video source needs its frame range.", nameof(source));
        var file = new FileInfo(source.Path);
        if (!Path.IsPathFullyQualified(source.Path) || !file.Exists)
            throw new CoreException("video.unreadable", "The video file is missing.");
        return string.Create(CultureInfo.InvariantCulture,
            $"video:{source.Path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{range.StartMs}|{range.IntervalMs}|{range.Count}");
    }

    private static IEnumerable<ImportItem> ExpandFolder(string root, bool recursive, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new CoreException("images.folder_unavailable", "The selected folder is missing or is not a folder.");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        var found = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        try
        {
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if (recursive) pending.Push(entry.FullName);
                    }
                    else if (Extensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(entry.FullName);
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoreException("images.folder_unavailable", "The selected folder cannot be read.", error);
        }
        return found
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path)))
            .OrderBy(item => item.Relative, ImageNameOrder.Instance)
            .Select(item => new ImportItem(item.Path, Shorten(item.Relative.Replace('\\', '/'))));
    }

    private static string Shorten(string name) => name.Length <= MaxNameLength ? name : name[..MaxNameLength];
}
