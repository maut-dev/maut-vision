using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces.Projects;
using MautVision.Desktop.Controllers.Projects.RequestDto;

namespace MautVision.Desktop.Mcp.Tools;

/// <summary>A resource of the server: its URI, name, description and the MIME type of its contents.</summary>
internal sealed record McpResourceInfo(string Uri, string Name, string Description, string MimeType);

/// <summary>
/// Read-only snapshots an AI client reads by link (resources/read): <c>maut://projects</c> — the project list, <c>maut://projects/{id}</c> —
/// a project with its overview. Contents are JSON, the same objects list_projects and get_project return.
/// </summary>
internal sealed class McpResources(McpToolDependencies dependencies, McpTools tools)
{
    internal const string Scheme = "maut://";
    internal const string ProjectsUri = "maut://projects";
    internal const string ProjectTemplate = "maut://projects/{projectId}";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal async Task<IReadOnlyList<McpResourceInfo>> ListAsync(CancellationToken cancellationToken)
    {
        var projects = await dependencies.Core.Projects.ListAsync(cancellationToken);
        return
        [
            new McpResourceInfo(ProjectsUri, "projects", "All projects of the library (JSON).", "application/json"),
            .. projects.Where(project => project.ArchivedAt is null).OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(project => new McpResourceInfo(ProjectsUri + "/" + project.Id, project.Name, "Project settings and overview (JSON).", "application/json")),
        ];
    }

    /// <summary>The JSON text of a resource; an unknown URI is mcp.resource_not_found.</summary>
    internal async Task<string> ReadAsync(string uri, CancellationToken cancellationToken)
    {
        if (uri == ProjectsUri)
        {
            var library = await dependencies.Projects.ListAsync(new ListProjectsRequestDto(), cancellationToken);
            return new JsonObject { ["projects"] = McpToolResult.Node(library.Items), ["lastProjectId"] = library.LastProjectId }.ToJsonString(Json);
        }
        if (uri.StartsWith(ProjectsUri + "/", StringComparison.Ordinal))
        {
            var id = uri[(ProjectsUri.Length + 1)..];
            if (id.Length > 0 && !id.Contains('/'))
            {
                var (project, overview) = await tools.ProjectSummaryAsync(Uri.UnescapeDataString(id), cancellationToken);
                return new JsonObject { ["project"] = McpToolResult.Node(project), ["overview"] = McpToolResult.Node(overview) }.ToJsonString(Json);
            }
        }
        if (DatasetManifest(uri) is { } manifest) return await tools.DatasetManifestAsync(manifest.ProjectId, manifest.VersionId, cancellationToken);
        throw new McpToolException("mcp.resource_not_found", "There is no such resource.", new Dictionary<string, string> { ["uri"] = uri });
    }

    internal const string DatasetManifestTemplate = "maut://projects/{projectId}/datasets/{versionId}/manifest";

    /// <summary>maut://projects/{projectId}/datasets/{versionId}/manifest, or null for another URI.</summary>
    private static (string ProjectId, Guid VersionId)? DatasetManifest(string uri)
    {
        if (!uri.StartsWith(ProjectsUri + "/", StringComparison.Ordinal)) return null;
        var parts = uri[(ProjectsUri.Length + 1)..].Split('/');
        return parts is [{ Length: > 0 } projectId, "datasets", var version, "manifest"] && Guid.TryParse(version, out var versionId)
            ? (Uri.UnescapeDataString(projectId), versionId)
            : null;
    }
}
