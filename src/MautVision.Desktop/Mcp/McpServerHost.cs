using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MautVision.Desktop.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MautVision.Desktop.Mcp;

/// <summary>
/// The Streamable HTTP endpoint <c>http://127.0.0.1:&lt;port&gt;/mcp</c>: Kestrel inside the host with the official MCP SDK. Only this class
/// touches ASP.NET Core and the SDK, and it is created only when the user turns the server on, so the application without MCP never loads
/// their assemblies. Every request passes the guard first: Host and Origin must be the loopback (protection from DNS rebinding), then
/// <c>Authorization: Bearer &lt;token&gt;</c> must match the token of the settings.
/// </summary>
internal sealed class McpServerHost : IAsyncDisposable
{
    internal const string Path = "/mcp";
    private readonly WebApplication application;

    private McpServerHost(WebApplication application) => this.application = application;

    /// <param name="sessionEnded">A stateful session ended (its uploads go).</param>
    internal static async Task<McpServerHost> StartAsync(int port, string token, McpToolbox toolbox, McpResources resources,
        IReadOnlyList<McpPromptDefinition> prompts, ILoggerFactory loggers, Action<string> sessionEnded, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ApplicationName = "MautVision.Mcp", ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
            // upload_file carries up to 10 MB in base64 (about 13.4 MB) inside JSON.
            options.Limits.MaxRequestBodySize = 16L * 1024 * 1024;
        });
        builder.Services.AddRoutingCore();
        builder.Services.AddSingleton<ILoggerFactory>(new QuietLoggerFactory(loggers));
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        var version = typeof(McpServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "maut-vision", Title = "Maut Vision", Version = version };
                options.ServerInstructions = string.Join(' ', Instructions.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                options.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = false, Subscribe = false },
                    Prompts = new PromptsCapability { ListChanged = false },
                };
                var handlers = options.Handlers;
                handlers.ListPromptsHandler = (_, _) => ValueTask.FromResult(new ListPromptsResult
                {
                    Prompts = prompts.Select(prompt => new Prompt
                    {
                        Name = prompt.Name, Title = prompt.Title, Description = prompt.Description,
                        Arguments = prompt.Arguments.Select(argument => new PromptArgument
                        {
                            Name = argument.Name, Description = argument.Description, Required = argument.Required,
                        }).ToList(),
                    }).ToList(),
                });
                handlers.GetPromptHandler = async (request, cancellation) =>
                {
                    var name = request.Params?.Name ?? "";
                    var prompt = prompts.FirstOrDefault(item => item.Name == name)
                        ?? throw new McpProtocolException($"mcp.prompt_unknown: There is no prompt “{name}”.", McpErrorCode.InvalidParams);
                    var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var (key, value) in request.Params?.Arguments ?? new Dictionary<string, JsonElement>())
                    {
                        if (value.ValueKind == JsonValueKind.String) arguments[key] = value.GetString()!;
                        else if (value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) arguments[key] = value.GetRawText();
                    }
                    try
                    {
                        var text = await prompt.Render(arguments, cancellation);
                        return new GetPromptResult
                        {
                            Description = prompt.Description,
                            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
                        };
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var failure = McpErrors.From(error, loggers.CreateLogger<McpServerHost>(), "prompts/get");
                        throw new McpProtocolException($"{failure.Code}: {failure.Message}", McpErrorCode.InvalidParams);
                    }
                };
                handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = toolbox.Tools.Select(Map).ToList() });
                handlers.CallToolHandler = async (request, cancellation) => await CallAsync(toolbox, request, cancellation);
                handlers.ListResourcesHandler = async (_, cancellation) => new ListResourcesResult
                {
                    Resources = (await resources.ListAsync(cancellation)).Select(item => new Resource
                    {
                        Uri = item.Uri, Name = item.Name, Description = item.Description, MimeType = item.MimeType,
                    }).ToList(),
                };
                handlers.ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(new ListResourceTemplatesResult
                {
                    ResourceTemplates =
                    [
                        new ResourceTemplate
                        {
                            UriTemplate = McpResources.ProjectTemplate, Name = "project", MimeType = "application/json",
                            Description = "Project settings and overview by projectId (JSON).",
                        },
                        new ResourceTemplate
                        {
                            UriTemplate = McpResources.DatasetManifestTemplate, Name = "dataset-manifest", MimeType = "application/json",
                            Description = "Composition of a dataset version: specification, classes, counts and every sample with its split, group and boxes (JSON).",
                        },
                    ],
                });
                handlers.ReadResourceHandler = async (request, cancellation) =>
                {
                    var uri = request.Params?.Uri ?? "";
                    try
                    {
                        var text = await resources.ReadAsync(uri, cancellation);
                        return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = text }] };
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var failure = McpErrors.From(error, loggers.CreateLogger<McpServerHost>(), "resources/read");
                        throw new McpProtocolException($"{failure.Code}: {failure.Message}",
                            failure.Code == "mcp.resource_not_found" ? McpErrorCode.ResourceNotFound : McpErrorCode.InvalidParams);
                    }
                };
            })
            .WithHttpTransport(transport =>
            {
                transport.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
#pragma warning disable MCPEXP002
                transport.RunSessionHandler = async (_, server, cancellation) =>
                {
                    try
                    {
                        await server.RunAsync(cancellation);
                    }
                    finally
                    {
                        if (server.SessionId is { } session) sessionEnded(session);
                    }
                };
#pragma warning restore MCPEXP002
            });
        var application = builder.Build();
        application.Use(Guard(port, token));
        application.MapMcp(Path);
        try
        {
            await application.StartAsync(cancellationToken);
        }
        catch
        {
            await application.DisposeAsync();
            throw;
        }
        return new McpServerHost(application);
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await application.StopAsync(stop.Token);
        }
        finally
        {
            await application.DisposeAsync();
        }
    }

    private const string Instructions = """
        Maut Vision trains YOLO object detectors on this computer. A project holds classes and variants, image groups with annotation,
        backgrounds, generation presets, dataset versions, training runs and model versions. Start with get_app_info and list_projects; pass
        projectId to every project tool. Long operations return a jobId at once: follow them with wait_job (up to 120 s per call) or get_job.
        Use view and view_many to look at images. Errors carry a stable code and an English message. The user sees everything you do in the
        application window and confirms deleting a project there.
        """;

    private static Tool Map(McpToolDefinition tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations = new ToolAnnotations
        {
            Title = tool.Title,
            ReadOnlyHint = tool.Class == McpToolClass.Read,
            DestructiveHint = tool.Class == McpToolClass.Destructive,
            IdempotentHint = tool.Idempotent,
            OpenWorldHint = false,
        },
    };

    private static async Task<CallToolResult> CallAsync(McpToolbox toolbox, RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var server = request.Server;
        var parameters = request.Params ?? throw new McpProtocolException("Missing parameters.", McpErrorCode.InvalidParams);
        Func<double, double?, string?, Task>? progress = null;
        if (parameters.ProgressToken is { } progressToken)
        {
            progress = (value, total, message) => server.NotifyProgressAsync(progressToken,
                new ProgressNotificationValue { Progress = (float)value, Total = total is { } known ? (float)known : null, Message = message },
                cancellationToken: cancellationToken);
        }
        var client = server.ClientInfo?.Name is { Length: > 0 } name ? name : "unknown client";
        var outcome = await toolbox.CallAsync(parameters.Name, parameters.Arguments, client, server.SessionId ?? "stateless", progress, cancellationToken);
        if (outcome.Error is { } error)
        {
            var body = JsonSerializer.SerializeToElement(new ErrorBody(error.Code, error.Message, error.Details), Json);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = body.GetRawText() }], StructuredContent = body };
        }
        var result = outcome.Result!;
        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
        foreach (var image in result.Images ?? []) content.Add(ImageContentBlock.FromBytes(image.Data, image.MimeType));
        return new CallToolResult { Content = content, StructuredContent = JsonSerializer.SerializeToElement(result.Structured, Json) };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record ErrorBody(string Code, string Message, IReadOnlyDictionary<string, string>? Details);

    /// <summary>
    /// Only local pages and programs reach the server: Host must name the loopback with this port and an Origin, when a browser sends one,
    /// must be a loopback origin (DNS rebinding); then the Bearer token must match, compared in constant time.
    /// </summary>
    private static Func<HttpContext, RequestDelegate, Task> Guard(int port, string token)
    {
        var expected = Encoding.UTF8.GetBytes("Bearer " + token);
        return async (context, next) =>
        {
            var request = context.Request;
            if (!IsLoopbackHost(request.Host, port) || (request.Headers.Origin.ToString() is { Length: > 0 } origin && !IsLoopbackOrigin(origin)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var presented = Encoding.UTF8.GetBytes(request.Headers.Authorization.ToString());
            if (!CryptographicOperations.FixedTimeEquals(presented, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        };
    }

    private static bool IsLoopbackHost(HostString host, int port)
        => host.Port == port && host.Host is "127.0.0.1" or "localhost" or "[::1]";

    internal static bool IsLoopbackOrigin(string origin)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host is "127.0.0.1" or "localhost" or "[::1]"
            && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query);

    /// <summary>The application log for the server: ASP.NET Core and the SDK only from warnings, the MCP host from information.</summary>
    private sealed class QuietLoggerFactory(ILoggerFactory inner) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName)
        {
            var logger = inner.CreateLogger(categoryName);
            // The host reports a busy port itself; the hosting stack would log the same failure as an error with its stack.
            if (categoryName.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal)) return new MinimumLevelLogger(logger, LogLevel.Critical);
            return categoryName.StartsWith("Microsoft.", StringComparison.Ordinal) || categoryName.StartsWith("ModelContextProtocol", StringComparison.Ordinal)
                ? new MinimumLevelLogger(logger, LogLevel.Warning)
                : logger;
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
            // The application log belongs to the application.
        }
    }

    private sealed class MinimumLevelLogger(ILogger inner, LogLevel minimum) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum && inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
