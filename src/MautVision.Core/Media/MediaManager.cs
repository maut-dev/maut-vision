using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Media;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Media;

/// <summary>
/// Video tools of the application: ffprobe for the properties of a video, ffmpeg for its frames (<see cref="VideoFrameReader"/>).
/// Shared by every area that takes frames from a video (images now, backgrounds later), so it lives at the Core level. The
/// tools are read from the delivery folder the host passes; only local files are read (<c>-protocol_whitelist file</c>).
/// </summary>
internal sealed class MediaManager(string? ffmpegDirectory, ILogger<MediaManager> logger) : IMediaManager
{
    internal const string UnreadableCode = "video.unreadable";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private const int HashBufferSize = 1 << 20;

    /// <summary>Decoder threads of one ffmpeg run: the import job holds one CPU slot; more than 6–8 threads do not decode faster.</summary>
    internal static int DecoderThreads { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    internal ILogger Logger => logger;

    internal string Ffmpeg => Tool("ffmpeg.exe");

    private string Ffprobe => Tool("ffprobe.exe");

    public async Task<VideoInfo> ProbeVideoAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Video path must be absolute.", nameof(path));
        if (!VideoFormats.IsVideo(path)) throw new CoreException("video.unsupported", $"'{Path.GetExtension(path)}' is not a supported video extension.");
        var executable = Ffprobe;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CoreException(UnreadableCode, "The video file is missing, empty or not a regular file.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        await using var process = ToolProcess.Start(executable, [
            "-v", "error", "-protocol_whitelist", "file", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height,avg_frame_rate,duration:stream_side_data=rotation:format=duration",
            "-of", "json", path,
        ]);
        string json;
        int exitCode;
        string errors;
        using (timeout.Token.Register(process.Kill))
        {
            try
            {
                using var reader = new StreamReader(process.Output);
                json = await reader.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                (exitCode, errors) = await process.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CoreException(UnreadableCode, "ffprobe did not answer in time.");
            }
        }
        if (exitCode != 0)
        {
            logger.LogWarning("ffprobe failed with exit code {ExitCode}: {Errors}", exitCode, errors);
            throw new CoreException(UnreadableCode, "ffprobe could not read the video.");
        }
        return Parse(json) ?? throw new CoreException(UnreadableCode, "The file has no video stream with a size and a duration.");
    }

    /// <summary>SHA-256 of the whole file, read once in large sequential blocks (the platform hash uses the CPU's SHA extensions).</summary>
    internal static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[HashBufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string Tool(string name)
    {
        var path = ffmpegDirectory is null ? null : Path.Combine(ffmpegDirectory, name);
        if (path is null || !File.Exists(path))
            throw new CoreException("video.ffmpeg_missing", $"{name} is missing in '{ffmpegDirectory}'.");
        return path;
    }

    private static VideoInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0) return null;
            var stream = streams[0];
            var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
            var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
            var seconds = Seconds(stream, "duration") ?? (root.TryGetProperty("format", out var format) ? Seconds(format, "duration") : null);
            if (width <= 0 || height <= 0 || seconds is not { } duration || !double.IsFinite(duration) || duration * 1000 < 1 || duration * 1000 > long.MaxValue / 2)
                return null;
            // ffmpeg applies the display rotation when it decodes, so a portrait phone video gives portrait frames.
            if (stream.TryGetProperty("side_data_list", out var sideData)
                && sideData.EnumerateArray().Any(item => item.TryGetProperty("rotation", out var rotation) && Math.Abs(rotation.GetDouble()) % 180 == 90))
                (width, height) = (height, width);
            return new VideoInfo(width, height, (long)Math.Floor(duration * 1000), Rate(stream), stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : "");
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static double? Seconds(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

    private static double Rate(JsonElement stream)
    {
        if (!stream.TryGetProperty("avg_frame_rate", out var value) || value.GetString() is not { } text) return 0;
        var parts = text.Split('/');
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0
            ? Math.Round(numerator / denominator, 3)
            : 0;
    }
}
