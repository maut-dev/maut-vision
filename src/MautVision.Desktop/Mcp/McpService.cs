using System.IO;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using MautVision.Desktop.Host;
using MautVision.Desktop.Mcp.Tools;
using Microsoft.Extensions.Logging;

namespace MautVision.Desktop.Mcp;

/// <summary>State of the server: stopped, starting, running, port_busy (another program holds the port), failed.</summary>
internal sealed record McpStatus(string State, int? Port, string? ErrorCode);

/// <summary>
/// Cleanup by time while the server runs, for clients served per request (revision 2026-07-28), whose session never ends: uploads older than
/// <paramref name="UploadAge"/> are removed and live checks no MCP request touched for <paramref name="CheckIdle"/> stop. The sweep runs every
/// <paramref name="Interval"/>.
/// </summary>
internal sealed record McpCleanup(TimeSpan UploadAge, TimeSpan CheckIdle, TimeSpan Interval)
{
    internal static McpCleanup Default { get; } = new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
}

/// <summary>
/// The MCP server of the application: its settings (host-settings.json), its state, the call log, confirmations and uploads. It starts and
/// stops Kestrel when the user changes the settings, without restarting the application. Without MCP enabled nothing of ASP.NET Core or the
/// SDK is loaded: <see cref="McpServerHost"/> is touched only in <see cref="StartHostAsync"/>, and the server starts only after the library
/// is shown, so the startup of the application does not change.
/// </summary>
internal sealed class McpService : IAsyncDisposable
{
    internal const string ProxyFileName = "maut-vision-mcp.exe";

    private readonly HostSettings hostSettings;
    private readonly Func<McpToolDependencies> dependencies;
    private readonly ILoggerFactory loggers;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private McpSettings settings;
    private IAsyncDisposable? server;
    private McpTools? tools;
    private McpStatus status = new("stopped", null, null);
    private McpCleanup cleanup = McpCleanup.Default;
    private CancellationTokenSource? sweep;
    private TaskCompletionSource cleanupChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;

    /// <param name="dependencies">What the tools work with; built on the first start.</param>
    /// <param name="requestAttention">Shows the window (from the tray too) for a confirmation.</param>
    internal McpService(HostSettings hostSettings, string workDirectory, Func<McpToolDependencies> dependencies, Action requestAttention, ILoggerFactory loggers)
    {
        this.hostSettings = hostSettings;
        this.dependencies = dependencies;
        this.loggers = loggers;
        logger = loggers.CreateLogger<McpService>();
        settings = (hostSettings.Mcp ?? McpSettings.Default).Normalized();
        CallLog = new McpCallLog(loggers.CreateLogger("MautVision.Desktop.Mcp.Calls"));
        Confirmations = new McpConfirmations(requestAttention);
        Uploads = new McpUploads(Path.Combine(workDirectory, McpUploads.FolderName), logger);
        Paths = new McpPaths(() => Settings, Uploads.Directory);
    }

    internal McpSettings Settings => Volatile.Read(ref settings);

    internal McpStatus Status => Volatile.Read(ref status);

    internal McpCallLog CallLog { get; }

    internal McpConfirmations Confirmations { get; }

    internal McpUploads Uploads { get; }

    internal McpPaths Paths { get; }

    /// <summary>The cleanup rules; the smoke shortens them (a change wakes the sweep at once).</summary>
    internal McpCleanup Cleanup
    {
        get => Volatile.Read(ref cleanup);
        set
        {
            Volatile.Write(ref cleanup, value);
            Interlocked.Exchange(ref cleanupChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    /// <summary>Settings or state changed; may come from any thread.</summary>
    internal event Action? Changed;

    /// <summary>The stdio proxy for clients that speak only stdio (Claude Desktop), shipped next to the application.</summary>
    internal static string ProxyPath => Path.Combine(AppContext.BaseDirectory, ProxyFileName);

    /// <summary>After the library is shown: leftovers of uploads go, and the server starts when the user left it on.</summary>
    internal async Task StartIfEnabledAsync(CancellationToken cancellationToken)
    {
        if (!Settings.Enabled)
        {
            // Uploads of a run that ended abnormally; a cheap check when there are none.
            if (Directory.Exists(Uploads.Directory)) await Task.Run(Uploads.Clear, cancellationToken);
            return;
        }
        await ApplyAsync(Settings, cancellationToken);
    }

    /// <summary>Saves new settings from the settings dialog and restarts the server when its endpoint, token or state changed.</summary>
    internal async Task<McpSettings> UpdateAsync(Func<McpSettings, McpSettings> change, CancellationToken cancellationToken)
    {
        var next = change(Settings).Normalized();
        if (next.Enabled && next.Token is null) next = next with { Token = McpSettings.NewToken() };
        await ApplyAsync(next, cancellationToken);
        return Settings;
    }

    private async Task ApplyAsync(McpSettings next, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = Settings;
            // The first enable (no token yet) with the default port that cannot be bound moves to a free port of a stable range once;
            // later a busy saved port is reported as port_busy and never changed silently.
            if (next.Enabled && previous.Token is null && next.Port == McpSettings.DefaultPort && !CanBind(next.Port)
                && FirstFreePort() is { } free)
            {
                logger.LogInformation("MCP default port {Port} cannot be bound; the first enable uses port {Free}.", next.Port, free);
                next = next with { Port = free };
            }
            if (next.Enabled && next.Token is null) next = next with { Token = McpSettings.NewToken() };
            Volatile.Write(ref settings, next);
            if (next != previous || hostSettings.Mcp is null) hostSettings.SaveMcp(next);
            var restart = server is null || next.Port != previous.Port || next.Token != previous.Token;
            if (!next.Enabled)
            {
                await StopHostAsync();
                SetStatus(new McpStatus("stopped", null, null));
            }
            else if (restart)
            {
                await StopHostAsync();
                SetStatus(new McpStatus("starting", next.Port, null));
                SetStatus(await StartHostAsync(next, cancellationToken));
            }
            else
            {
                Changed?.Invoke();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private void SetStatus(McpStatus next)
    {
        Volatile.Write(ref status, next);
        Changed?.Invoke();
    }

    /// <summary>The only place that loads ASP.NET Core and the SDK; not inlined, so its callers do not load them while compiled.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<McpStatus> StartHostAsync(McpSettings current, CancellationToken cancellationToken)
    {
        try
        {
            Uploads.Clear();
            var context = dependencies();
            var created = new McpTools(context);
            var toolbox = new McpToolbox(created.Create(), () => Settings, Confirmations, CallLog, logger);
            var resources = new McpResources(context, created);
            // A session that ends takes its uploads and stops the live checks it started.
            server = await McpServerHost.StartAsync(current.Port, current.Token!, toolbox, resources, created.CreatePrompts(), loggers, session =>
            {
                Uploads.EndSession(session);
                created.EndSession(session);
            }, cancellationToken);
            tools = created;
            sweep = new CancellationTokenSource();
            _ = SweepAsync(created, sweep.Token);
            logger.LogInformation("MCP server listens on http://127.0.0.1:{Port}{Path}.", current.Port, McpServerHost.Path);
            return new McpStatus("running", current.Port, null);
        }
        catch (Exception error) when (IsAddressInUse(error))
        {
            logger.LogWarning("MCP server port {Port} is busy.", current.Port);
            return new McpStatus("port_busy", current.Port, "mcp.port_busy");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogError(error, "MCP server did not start on port {Port}.", current.Port);
            return new McpStatus("failed", current.Port, "mcp.server_failed");
        }
    }

    /// <summary>Whether a listener can take the loopback port now (another program or a Windows reservation may hold it).</summary>
    internal static bool CanBind(int port)
    {
        try
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The first port of <see cref="McpSettings.FallbackPorts"/> that can be bound; null when none can.</summary>
    internal static int? FirstFreePort()
    {
        for (var port = McpSettings.FallbackPorts.Start.Value; port < McpSettings.FallbackPorts.End.Value; port++)
        {
            if (CanBind(port)) return port;
        }
        return null;
    }

    private static bool IsAddressInUse(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied }) return true;
            if (current.GetType().Name == "AddressInUseException") return true;
        }
        return false;
    }

    /// <summary>Every <see cref="McpCleanup.Interval"/> while the server runs: old uploads go, idle live checks stop.</summary>
    private async Task SweepAsync(McpTools owner, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var rules = Cleanup;
            try
            {
                await Task.WhenAny(Task.Delay(rules.Interval, cancellationToken), Volatile.Read(ref cleanupChanged).Task).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            rules = Cleanup;
            try
            {
                var uploads = Uploads.RemoveOlderThan(rules.UploadAge);
                var checks = owner.StopIdleChecks(rules.CheckIdle);
                if (uploads + checks > 0) logger.LogInformation("MCP cleanup: {Uploads} old uploads removed, {Checks} idle live checks released (running ones stop).", uploads, checks);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "MCP cleanup failed.");
            }
        }
    }

    private async Task StopHostAsync()
    {
        // Not disposed: the sweep may still be reading its token; the source holds no timer.
        sweep?.Cancel();
        sweep = null;
        if (server is not { } running) return;
        server = null;
        Confirmations.DenyAll();
        tools?.EndAllSessions();
        tools = null;
        try
        {
            await running.DisposeAsync();
            logger.LogInformation("MCP server stopped.");
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "MCP server did not stop cleanly.");
        }
        Uploads.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            await StopHostAsync();
        }
        finally
        {
            gate.Release();
        }
    }
}
