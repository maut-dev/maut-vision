using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Backgrounds;

namespace MautVision.Core.Projects.Backgrounds;

/// <summary>
/// One source of a run: a file with its optional crop, or one frame of the video (Index is its place in the frame range,
/// TimestampMs its timestamp). Index also selects the blur radius and the derived background ID.
/// </summary>
internal sealed record BackgroundSourceItem(int Index, string Name, string Path, BackgroundCrop? Crop, long? TimestampMs);

/// <summary>
/// Expands the input of a run into its ordered sources. Digest identifies them, so a run resumed after a restart detects a
/// replaced video (path, size, write time and range) or another list of files.
/// </summary>
internal static class BackgroundImportPlan
{
    internal const int MaxNameLength = 240;

    internal static (IReadOnlyList<BackgroundSourceItem> Items, string Digest) Expand(ImportBackgroundsInput input)
    {
        var items = new List<BackgroundSourceItem>();
        var digest = new StringBuilder();
        if (input.Video is { } video)
        {
            var file = new FileInfo(video.Path);
            if (!Path.IsPathFullyQualified(video.Path) || !file.Exists) throw new CoreException("video.unreadable", "The video file is missing.");
            var range = video.Frames;
            digest.Append(CultureInfo.InvariantCulture, $"video:{video.Path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{range.StartMs}|{range.IntervalMs}|{range.Count}");
            var name = Shorten(Path.GetFileName(video.Path));
            for (var index = 0; index < range.Count; index++)
            {
                items.Add(new BackgroundSourceItem(index, name, video.Path, video.Region, range.TimestampMs(index)));
            }
        }
        else
        {
            for (var index = 0; index < input.Files.Count; index++)
            {
                var source = input.Files[index];
                items.Add(new BackgroundSourceItem(index, Shorten(Path.GetFileName(source.Path)), source.Path, source.Crop, null));
                digest.Append(source.Path).Append('\n');
            }
        }
        return (items, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString()))));
    }

    /// <summary>The whole video must be at least the set size, and the one region must lie inside its frames.</summary>
    internal static void CheckVideo(VideoInfo video, InputSize target, BackgroundCrop region)
    {
        if (video.Width < target.Width || video.Height < target.Height)
            throw new CoreException("backgrounds.video_too_small", $"Video {video.Width}x{video.Height} is smaller than the set size {target.Width}x{target.Height}.");
        if ((long)region.Left + target.Width > video.Width || (long)region.Top + target.Height > video.Height)
            throw new CoreException("backgrounds.region_invalid", $"Region at {region.Left},{region.Top} does not fit video {video.Width}x{video.Height}.");
    }

    /// <summary>Up to count indexes of [0, total), evenly over the whole range: samples see the beginning and the end.</summary>
    internal static IReadOnlyList<int> Spread(int total, int count)
    {
        count = Math.Min(count, total);
        if (count <= 0) return [];
        if (count == 1) return [0];
        return Enumerable.Range(0, count).Select(index => (int)((long)index * (total - 1) / (count - 1))).Distinct().ToArray();
    }

    internal static string Shorten(string name) => name.Length <= MaxNameLength ? name : name[..MaxNameLength];
}
