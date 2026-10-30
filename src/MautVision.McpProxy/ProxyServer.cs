using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MautVision.McpProxy;

/// <summary>
/// The stdio side: the server of the official SDK with the identity, instructions and capabilities of the application, whose handlers pass each
/// request to the application unchanged and return its result unchanged (tools with structured and image content, resources, templates,
/// prompts). The SDK of each side speaks the protocol revision its peer chose, so the client and the application need not agree on one.
/// </summary>
internal static class ProxyServer
{
    internal static McpServerOptions Options(McpClient application, Upstream upstream)
    {
        var capabilities = application.ServerCapabilities;
        var options = new McpServerOptions
        {
            ServerInfo = application.ServerInfo,
            ServerInstructions = application.ServerInstructions,
            Capabilities = new ServerCapabilities { Tools = capabilities.Tools, Resources = capabilities.Resources, Prompts = capabilities.Prompts },
        };
        var handlers = options.Handlers;
        handlers.ListToolsHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.ListToolsAsync(value, token), cancellation);
        handlers.CallToolHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.CallToolAsync(value, token), cancellation);
        handlers.ListResourcesHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.ListResourcesAsync(value, token), cancellation);
        handlers.ListResourceTemplatesHandler = (request, cancellation)
            => Forward(upstream, request, (client, value, token) => client.ListResourceTemplatesAsync(value, token), cancellation);
        handlers.ReadResourceHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.ReadResourceAsync(value, token), cancellation);
        handlers.ListPromptsHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.ListPromptsAsync(value, token), cancellation);
        handlers.GetPromptHandler = (request, cancellation) => Forward(upstream, request, (client, value, token) => client.GetPromptAsync(value, token), cancellation);
        return options;
    }

    private static async ValueTask<TResult> Forward<TParams, TResult>(Upstream upstream, RequestContext<TParams> request,
        Func<McpClient, TParams, CancellationToken, ValueTask<TResult>> call, CancellationToken cancellationToken)
        where TParams : RequestParams
    {
        var parameters = WithoutRevisionMeta(request.Params ?? Empty<TParams>());
        try
        {
            return await upstream.CallAsync(request.Server, async (client, token) => await call(client, parameters, token), cancellationToken);
        }
        catch (ProxyException error)
        {
            // The application went away and cannot be started: the client shows the reason with the failed request.
            Console.Error.WriteLine($"maut-vision-mcp: {error.Message}");
            throw new McpProtocolException(error.Message, McpErrorCode.InternalError);
        }
    }

    /// <summary>A list request may come without params; the others cannot.</summary>
    private static TParams Empty<TParams>() where TParams : RequestParams => (TParams)(object)(typeof(TParams).Name switch
    {
        nameof(ListToolsRequestParams) => new ListToolsRequestParams(),
        nameof(ListResourcesRequestParams) => new ListResourcesRequestParams(),
        nameof(ListResourceTemplatesRequestParams) => new ListResourceTemplatesRequestParams(),
        nameof(ListPromptsRequestParams) => new ListPromptsRequestParams(),
        _ => throw new McpProtocolException("Missing parameters.", McpErrorCode.InvalidParams),
    });

    /// <summary>
    /// The per-request fields of the 2026-07-28 revision (<c>io.modelcontextprotocol/*</c>) describe the downstream session; the client of the
    /// other side writes its own. The rest of <c>_meta</c> (the progress token among others) goes on unchanged.
    /// </summary>
    private static TParams WithoutRevisionMeta<TParams>(TParams parameters) where TParams : RequestParams
    {
        if (parameters.Meta is not { } meta || !meta.Any(item => item.Key.StartsWith("io.modelcontextprotocol/", StringComparison.Ordinal))) return parameters;
        var kept = new JsonObject();
        foreach (var (key, value) in meta)
        {
            if (!key.StartsWith("io.modelcontextprotocol/", StringComparison.Ordinal)) kept[key] = value?.DeepClone();
        }
        parameters.Meta = kept;
        return parameters;
    }
}
