using System.Diagnostics;
using System.Text;

namespace MautVision.Core.Media;

/// <summary>
/// One ffmpeg or ffprobe run: started with an argument list (never through a shell), without a window, with stdin closed and
/// stderr drained in the background (its tail is kept for the log). Disposal kills the whole process tree when it is still
/// running and waits for its exit, so cancellation leaves no orphan. If the host itself dies, the tool fails on its next
/// write to the closed stdout pipe and exits.
/// </summary>
internal sealed class ToolProcess : IAsyncDisposable
{
    private const int TailLength = 4096;
    private readonly Process process;
    private readonly Task<string> errors;

    private ToolProcess(Process process)
    {
        this.process = process;
        errors = DrainAsync(process.StandardError);
    }

    internal Stream Output => process.StandardOutput.BaseStream;

    internal int Id => process.Id;

    internal static ToolProcess Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        process.StandardInput.Close();
        return new ToolProcess(process);
    }

    /// <summary>Kills the process tree; safe to call from a cancellation callback, also after the exit.</summary>
    internal void Kill()
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already exited or exiting.
        }
    }

    /// <summary>Waits for the exit and returns the exit code with the tail of stderr.</summary>
    internal async Task<(int ExitCode, string Errors)> WaitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await errors.ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                tail.Append(buffer, 0, read);
                if (tail.Length > 2 * TailLength) tail.Remove(0, tail.Length - TailLength);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The pipe closed with the killed process.
        }
        return tail.Length <= TailLength ? tail.ToString().Trim() : tail.ToString(tail.Length - TailLength, TailLength).Trim();
    }
}
