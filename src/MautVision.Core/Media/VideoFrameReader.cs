using System.Buffers;
using System.Globalization;
using System.Text;
using MautVision.Core.Interfaces.Media;
using Microsoft.Extensions.Logging;

namespace MautVision.Core.Media;

/// <summary>A decoded frame: RGBA 8-bit, unpremultiplied, rows without padding, in a pooled buffer returned on disposal.</summary>
internal sealed class VideoFrame(int width, int height, byte[] buffer) : IDisposable
{
    private byte[]? buffer = buffer;

    internal int Width { get; } = width;

    internal int Height { get; } = height;

    internal int Length => Width * Height * 4;

    internal byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref buffer, null) is { } returned) ArrayPool<byte>.Shared.Return(returned);
    }
}

/// <summary>
/// Frames of one video at the timestamps of a <see cref="VideoFrameRange"/>. The frame of a timestamp T is the first frame
/// whose time is not before T − 0.5 ms; the same rule holds for every way of reading, so a run resumed from any index gets
/// the same frames.
/// <para>
/// Up to a step of <see cref="SeekIntervalMs"/> one ffmpeg process decodes the video once from the first wanted timestamp and
/// a <c>select</c> filter passes only the wanted frames: reading between close timestamps costs less than seeking. With a
/// larger step each frame is read by its own short run that seeks to it, because a pass would decode everything between the
/// timestamps. ffmpeg writes PAM (raw RGBA with a small size header) to a pipe; a frame is read only when a caller asks for
/// it, so ffmpeg waits on the pipe and at most the frames callers are waiting for are held.
/// </para>
/// Callers may ask concurrently and slightly out of order (parallel workers taking consecutive indexes); a frame read for
/// another waiting caller is kept until that caller takes it. Cancelling a read kills the process tree.
/// </summary>
internal sealed class VideoFrameReader(MediaManager media, string path, VideoFrameRange range, long seekAboveMs = VideoFrameReader.SeekIntervalMs)
    : IAsyncDisposable
{
    internal const long SeekIntervalMs = 3000;
    // Every run seeks this far before its first timestamp, so ffmpeg's own trimming at the seek point never decides a frame.
    private const long SeekLeadMs = 1000;
    // A request further ahead restarts the pass there instead of holding the frames in between.
    private const int MaxAhead = 16;
    private const int HeaderLimit = 256;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, VideoFrame> held = [];
    // Indexes of callers waiting for the gate.
    private readonly List<int> waiting = [];
    private ToolProcess? process;
    private Stream? output;
    private int next;
    private bool ended;

    /// <summary>ffmpeg runs started by this reader (smoke checks of the pass and seek paths).</summary>
    internal int Runs { get; private set; }

    /// <summary>The frame at <paramref name="index"/>, or null when the video ends before it (or the frame cannot be decoded).</summary>
    internal async Task<VideoFrame?> ReadAsync(int index, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, range.Count);
        lock (waiting) waiting.Add(index);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (waiting) waiting.Remove(index);
        }
        try
        {
            if (held.Remove(index, out var kept)) return kept;
            return range.IntervalMs > seekAboveMs
                ? await ReadSeekingAsync(index, cancellationToken).ConfigureAwait(false)
                : await ReadPassAsync(index, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            foreach (var frame in held.Values) frame.Dispose();
            held.Clear();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<VideoFrame?> ReadPassAsync(int index, CancellationToken cancellationToken)
    {
        if (process is null || index < next || index - next > MaxAhead)
        {
            await StopAsync().ConfigureAwait(false);
            // Callers still waiting for earlier frames are served by the same pass.
            var first = index;
            lock (waiting)
            {
                foreach (var other in waiting)
                {
                    if (other < first && !held.ContainsKey(other)) first = other;
                }
            }
            Start(first, range.Count - first);
        }
        var running = process!;
        try
        {
            using var kill = cancellationToken.Register(running.Kill);
            while (!ended)
            {
                var frame = await ReadFrameAsync(running, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    ended = true;
                    break;
                }
                var current = next++;
                if (current == index) return frame;
                // A concurrent caller asked for this one and waits for the gate.
                if (!held.TryAdd(current, frame)) frame.Dispose();
            }
            return null;
        }
        catch
        {
            // A cancelled or failed pass is not continued: the next read starts a new one.
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<VideoFrame?> ReadSeekingAsync(int index, CancellationToken cancellationToken)
    {
        Start(index, 1);
        var running = process!;
        try
        {
            using var kill = cancellationToken.Register(running.Kill);
            return await ReadFrameAsync(running, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    private void Start(int index, int count)
    {
        var from = range.TimestampMs(index);
        var seek = Math.Max(0, from - SeekLeadMs);
        var executable = media.Ffmpeg;
        // t is the frame time after the seek point; the filter keeps the first frame at or after each wanted timestamp.
        var select = string.Create(CultureInfo.InvariantCulture, $"select=gte(t*1000+0.5\\,{from - seek}+selected_n*{range.IntervalMs})");
        process = ToolProcess.Start(executable, [
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-threads", MediaManager.DecoderThreads.ToString(CultureInfo.InvariantCulture),
            "-ss", (seek / 1000.0).ToString("0.000", CultureInfo.InvariantCulture),
            "-protocol_whitelist", "file", "-i", path,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", select, "-fps_mode", "passthrough",
            "-frames:v", count.ToString(CultureInfo.InvariantCulture),
            "-c:v", "pam", "-pix_fmt", "rgba", "-f", "image2pipe", "pipe:1",
        ]);
        output = new BufferedStream(process.Output, 1 << 20);
        next = index;
        ended = false;
        Runs++;
    }

    private async Task StopAsync()
    {
        if (process is not { } running) return;
        process = null;
        output = null;
        if (!ended)
        {
            await running.DisposeAsync().ConfigureAwait(false);
            return;
        }
        var (exitCode, errors) = await running.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        if (exitCode != 0) media.Logger.LogWarning("ffmpeg ended with exit code {ExitCode} while reading frames: {Errors}", exitCode, errors);
        await running.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>One PAM image from the pipe; null at the end of the stream. Cancellation surfaces as OperationCanceledException.</summary>
    private async Task<VideoFrame?> ReadFrameAsync(ToolProcess running, CancellationToken cancellationToken)
    {
        var stream = output!;
        try
        {
            var header = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
            if (header is null) return null;
            var (width, height) = header.Value;
            var length = checked(width * height * 4);
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }
            return new VideoFrame(width, height, buffer);
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or OverflowException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            media.Logger.LogWarning(error, "Frames of process {ProcessId} ended early.", running.Id);
            return null;
        }
    }

    /// <summary>Reads "P7 / WIDTH / HEIGHT / DEPTH 4 / MAXVAL 255 / TUPLTYPE RGB_ALPHA / ENDHDR"; null when the stream ended before it.</summary>
    private static async Task<(int Width, int Height)?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                if (text.Length == 0) return null;
                throw new EndOfStreamException("The frame header was cut off.");
            }
            text.Append((char)one[0]);
            if (text.Length > HeaderLimit) throw new IOException("The frame header is too long.");
            if (one[0] == '\n' && text.ToString().EndsWith("ENDHDR\n", StringComparison.Ordinal)) break;
        }
        int width = 0, height = 0, depth = 0, maximum = 0;
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines[0] != "P7") throw new IOException("The frame is not a PAM image.");
        foreach (var line in lines)
        {
            var parts = line.Split(' ', 2);
            if (parts.Length != 2) continue;
            var value = int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
            switch (parts[0])
            {
                case "WIDTH": width = value; break;
                case "HEIGHT": height = value; break;
                case "DEPTH": depth = value; break;
                case "MAXVAL": maximum = value; break;
            }
        }
        if (width <= 0 || height <= 0 || depth != 4 || maximum != 255) throw new IOException("Unexpected PAM frame layout.");
        return (width, height);
    }
}
