using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Hardware;
using MautVision.Desktop.Controllers.Jobs;
using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Controllers.Projects.Overview;
using MautVision.Desktop.Host;

namespace MautVision.Desktop.Mcp.Tools;

/// <summary>
/// What the tools work with: the one Core of the process and the same controllers the UI calls, so an AI client and the user see the same
/// data, jobs and events. OpenProjectInApp shows a project in the window (the page navigates, the window comes to the front).
/// </summary>
internal sealed record McpToolDependencies(ICore Core, ProjectsController Projects, JobsController Jobs, OverviewController Overview,
    HardwareController Hardware, HostLanguage Language, McpPaths Paths, McpUploads Uploads, string WorkDirectory, Func<McpSettings> Settings,
    Action<string> OpenProjectInApp);

/// <summary>
/// The tools of the server, grouped by area in partial files. Each tool declares its class (read, write, destructive), its schemas and an
/// English description for the AI agent. Project tools take projectId explicitly: there is no hidden current project.
/// </summary>
internal sealed partial class McpTools(McpToolDependencies dependencies)
{
    private ICore Core => dependencies.Core;

    internal IReadOnlyList<McpToolDefinition> Create() =>
    [
        .. SystemTools(),
        .. JobTools(),
        .. FileTools(),
        .. ViewTools(),
        .. ProjectTools(),
        .. BaseModelTools(),
        .. CatalogTools(),
        .. ImageTools(),
        .. BackgroundTools(),
        .. GenerationTools(),
        .. DatasetTools(),
        .. TrainingTools(),
        .. ModelTools(),
        .. CheckTools(),
        .. AnalyticsTools(),
    ];

    /// <summary>Ready scenarios (prompts/get), grouped by area like the tools.</summary>
    internal IReadOnlyList<McpPromptDefinition> CreatePrompts() =>
    [
        .. ImagePrompts(),
        .. DatasetPrompts(),
        .. TrainingPrompts(),
    ];

    /// <summary>A project ready for work, prepared in the background: the user's current project and live checks are not touched.</summary>
    private Task<IProject> PrepareAsync(string projectId, CancellationToken cancellationToken) => Core.Projects.PrepareAsync(projectId, cancellationToken);

    private const string ProjectIdSchema = """{ "type": "string", "description": "Project ID (from list_projects)." }""";

    private static McpToolResult Result(JsonObject structured, string text, IReadOnlyList<McpImage>? images = null) => new(structured, text, images);

    private static JsonObject Wrap(string name, object? value) => new() { [name] = McpToolResult.Node(value) };
}
