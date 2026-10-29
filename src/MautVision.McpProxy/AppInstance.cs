using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MautVision.McpProxy;

/// <summary>A failure the user must act on; its message goes to stderr as is.</summary>
internal sealed class ProxyException(string message) : Exception(message);

/// <summary>The command line: only <c>--work-directory</c>; the token is never passed as an argument.</summary>
internal static class ProxyArguments
{
    internal static string WorkDirectory(string[] args)
    {
        var workDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MautVision");
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--work-directory" && index + 1 < args.Length) workDirectory = args[++index];
            else throw new ArgumentException($"Unknown argument {args[index]}.");
        }
        return Path.GetFullPath(workDirectory);
    }
}

/// <summary>The MCP part of host-settings.json the proxy needs.</summary>
internal sealed record ProxySettings(bool Enabled, int Port, string? Token, bool LaunchOnRequest)
{
    internal Uri Endpoint => new($"http://127.0.0.1:{Port}/mcp");
}

/// <summary>
/// The application of one work directory: its MCP settings, whether it runs (the single-instance mutex of MautVision.Desktop) and its start in
/// the notification area.
/// </summary>
internal sealed class AppInstance(string workDirectory, string applicationDirectory)
{
    internal const string AppFileName = "MautVision.Desktop.exe";
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private const string Dialog = "the \"AI assistants (MCP)\" dialog («Подключение ИИ (MCP)») in the header of Maut Vision";

    internal string WorkDirectory { get; } = workDirectory;

    /// <summary>Settings as host-settings.json keeps them now; a missing or unreadable file counts as "MCP off".</summary>
    internal ProxySettings ReadSettings()
    {
        var path = Path.Combine(WorkDirectory, "host-settings.json");
        try
        {
            if (!File.Exists(path)) return new ProxySettings(false, 0, null, true);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("mcp", out var mcp) || mcp.ValueKind != JsonValueKind.Object) return new ProxySettings(false, 0, null, true);
            bool Flag(string name, bool fallback) => mcp.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;
            var port = mcp.TryGetProperty("port", out var portValue) && portValue.TryGetInt32(out var number) ? number : 0;
            var token = mcp.TryGetProperty("token", out var tokenValue) && tokenValue.ValueKind == JsonValueKind.String ? tokenValue.GetString() : null;
            return new ProxySettings(Flag("enabled", false), port, token, Flag("launchOnRequest", true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // The application replaces the file atomically; a read during a replace is tried again by the caller.
            return new ProxySettings(false, 0, null, true);
        }
    }

    /// <summary>The application of this work directory holds its single-instance mutex (MautVision.Desktop/Host/SingleInstance).</summary>
    internal bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName(WorkDirectory), out var mutex)) return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>Same name as SingleInstance.Name of the host: the case-insensitive full path, hashed, in the session namespace.</summary>
    internal static string MutexName(string workDirectory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDirectory)).ToUpperInvariant();
        return @"Local\MautVision-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32];
    }

    /// <summary>
    /// Settings with which the endpoint should answer: starts the application in the tray when it is closed and the user allowed that, and
    /// explains what to do otherwise.
    /// </summary>
    internal ProxySettings EnsureStarted(ref bool launched)
    {
        var settings = ReadSettings();
        if (!settings.Enabled)
            throw new ProxyException($"The MCP server of Maut Vision is turned off. Turn it on in {Dialog}, then restart this MCP server in your client.");
        if (IsRunning() || launched) return settings;
        if (!settings.LaunchOnRequest)
            throw new ProxyException($"Maut Vision is not running and starting it on request is turned off in {Dialog}. Start Maut Vision, then retry.");
        Launch();
        launched = true;
        return settings;
    }

    private void Launch()
    {
        var executable = Path.Combine(applicationDirectory, AppFileName);
        if (!File.Exists(executable)) throw new ProxyException($"Maut Vision is not running and {executable} was not found next to the proxy. Start Maut Vision, then retry.");
        Console.Error.WriteLine($"maut-vision-mcp: starting Maut Vision in the notification area ({WorkDirectory}).");
        // Through the shell, so the application does not inherit the stdio pipes of the client and the client sees the proxy end.
        using var process = Process.Start(new ProcessStartInfo(executable, $"--tray --work-directory \"{Path.TrimEndingDirectorySeparator(WorkDirectory)}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = applicationDirectory,
        });
    }

    internal static string NotAnswering(ProxySettings settings, string? reason)
        => $"Maut Vision did not answer at {settings.Endpoint} within {StartTimeout.TotalSeconds:N0} s"
           + (reason is null ? "" : $" ({reason})")
           + $". Check the state of the server in {Dialog}: another program may hold the port.";
}
