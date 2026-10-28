using MautVision.Core.Interfaces;

namespace MautVision.Core;

public static class CoreFactory
{
    private static readonly object Gate = new();
    private static ICore? instance;
    private static CoreOptions? configuration;

    public static ICore GetInstance(CoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.LoggerFactory);
        var normalized = options with
        {
            WorkDirectory = Normalize(options.WorkDirectory),
            BaseModelCatalogPath = Normalize(options.BaseModelCatalogPath),
            FfmpegDirectory = options.FfmpegDirectory is null ? null : Normalize(options.FfmpegDirectory),
        };
        lock (Gate)
        {
            if (configuration is not null &&
                (!SamePath(configuration.WorkDirectory, normalized.WorkDirectory)
                || !SamePath(configuration.BaseModelCatalogPath, normalized.BaseModelCatalogPath)
                || !SamePath(configuration.FfmpegDirectory ?? "", normalized.FfmpegDirectory ?? "")
                || !Equals(configuration.PythonWorker, normalized.PythonWorker)))
                throw new InvalidOperationException("Core is already configured with different options.");
            instance ??= new Core(normalized);
            configuration = normalized;
            return instance;
        }
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
