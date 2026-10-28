namespace MautVision.Core.Resources;

/// <summary>Only these types are served; a file with any other extension is not a resource.</summary>
internal static class ResourceContentTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.Ordinal)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".gif"] = "image/gif",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
    };

    internal static string? Find(string extension) => ByExtension.GetValueOrDefault(extension);

    internal static bool IsImage(string contentType) => contentType.StartsWith("image/", StringComparison.Ordinal);
}
