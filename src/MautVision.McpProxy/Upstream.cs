using System.Net.Http;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MautVision.McpProxy;

/// <summary>
/// The connection to the application: an MCP client of the official SDK over Streamable HTTP with the Bearer token of the settings. It connects
/// on demand (starting the application when needed), takes the name of the downstream client, so jobs in the window read «ИИ (Claude) · …», and
/// connects again when the application was closed and started in between. Progress notifications of the application go to the downstream client
/// unchanged: their tokens are the downstream client's own.
/// </summary>
internal sealed class Upstream(AppInstance app) : IAsyncDisposable
{
    internal const string ProxyName = "maut-vision-mcp";

    private readonly SemaphoreSlim gate = new(1, 1);
    private McpClient? client;
    private string? clientName;

    /// <summary>The stdio server, once it runs: notifications of the application go there.</summary>
    internal McpServer? Downstream { get; set; }

    /// <summary>A connected client named after <paramref name="downstreamName"/> (the proxy's own name before the client is known).</summary>
    internal async Task<McpClient> GetAsync(string? downstreamName, CancellationToken cancellationToken)
    {
        var name = downstreamName is { Length: > 0 } known ? known : ProxyName;
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (client is not null && (clientName == name || downstreamName is null)) return client;
            await DropAsync();
            client = await ConnectAsync(name, cancellationToken);
            clientName = name;
            return client;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs a request; when the application cannot be reached (it was closed), connects again — starting it when allowed — and repeats once.
    /// Only a refused connection is repeated: the request never reached the application, so nothing runs twice.
    /// </summary>
    internal async Task<T> CallAsync<T>(McpServer server, Func<McpClient, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        var name = server.ClientInfo?.Name;
        var current = await GetAsync(name, cancellationToken);
        try
        {
            return await call(current, cancellationToken);
        }
        catch (Exception error) when (IsUnreachable(error))
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (ReferenceEquals(client, current)) await DropAsync();
            }
            finally
            {
                gate.Release();
            }
            return await call(await GetAsync(name, cancellationToken), cancellationToken);
        }
    }

    private async Task<McpClient> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var launched = false;
        string? reason = null;
        while (true)
        {
            var settings = app.EnsureStarted(ref launched);
            if (settings.Token is { Length: > 0 } token && settings.Port > 0)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromSeconds(15));
                    var connected = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                        {
                            Endpoint = settings.Endpoint,
                            TransportMode = HttpTransportMode.StreamableHttp,
                            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                            Name = ProxyName,
                        }), new McpClientOptions
                        {
                            ClientInfo = new Implementation { Name = name, Version = typeof(Upstream).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
                        }, cancellationToken: attempt.Token);
                    connected.RegisterNotificationHandler(NotificationMethods.ProgressNotification, ForwardAsync);
                    return connected;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    reason = Reason(error);
                }
            }
            if (watch.Elapsed > AppInstance.StartTimeout) throw new ProxyException(AppInstance.NotAnswering(settings, reason));
            await Task.Delay(500, cancellationToken);
        }
    }

    private async ValueTask ForwardAsync(JsonRpcNotification notification, CancellationToken cancellationToken)
    {
        if (Downstream is { } server) await server.SendMessageAsync(notification, cancellationToken);
    }

    private static bool IsUnreachable(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }) return true;
        }
        return false;
    }

    private static string Reason(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status }) return $"HTTP {(int)status}" + (status == System.Net.HttpStatusCode.Unauthorized ? ", the token was rejected" : "");
            if (current is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }) return "connection refused";
        }
        return error.Message;
    }

    private async Task DropAsync()
    {
        if (client is not { } previous) return;
        client = null;
        clientName = null;
        try
        {
            await previous.DisposeAsync();
        }
        catch (Exception)
        {
            // The application may be gone already.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DropAsync();
        gate.Dispose();
    }
}
