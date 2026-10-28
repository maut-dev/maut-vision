using System.IO;

namespace MautVision.Desktop;

internal static class WorkDirectorySetup
{
    internal static Task PrepareAsync(string directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, ".initial-data-prepared");
        if (File.Exists(marker)) return;
        var source = Path.Combine(AppContext.BaseDirectory, "appdata");
        // The build omits appdata while the seed set is empty.
        var files = Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(file) == ".gitkeep") continue;
            var target = Path.Combine(directory, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) File.Copy(file, target, overwrite: false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        File.WriteAllText(marker, "Initial application data prepared.");
    }, cancellationToken);
}
