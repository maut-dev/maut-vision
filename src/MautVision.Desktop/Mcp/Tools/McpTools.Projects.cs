using System.Globalization;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces;
using MautVision.Desktop.Controllers.Projects;
using MautVision.Desktop.Controllers.Projects.Overview.RequestDto;
using MautVision.Desktop.Controllers.Projects.RequestDto;
using MautVision.Desktop.Controllers.Projects.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string ProjectSchema = """
        { "type": "object", "description": "Project settings: id, name, description, baseModelId, inputSize {width, height}, revision (grows with each change), archivedAt (null when active).",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" }, "baseModelId": { "type": "string" },
            "inputSize": { "type": "object", "properties": { "width": { "type": "integer" }, "height": { "type": "integer" } } },
            "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] } },
          "required": ["id", "name", "baseModelId", "inputSize", "revision"] }
        """;

    private IEnumerable<McpToolDefinition> ProjectTools()
    {
        yield return McpToolDefinition.Create("list_projects", "List projects", McpToolClass.Read, true,
            """
            Projects of the library with their IDs, model input size and base model. Every project tool takes projectId from here; there is no
            hidden current project. Archived projects are skipped unless includeArchived=true. lastProjectId is the project the user opened last.
            """,
            """
            { "type": "object", "properties": { "includeArchived": { "type": "boolean", "default": false } }, "additionalProperties": false }
            """,
            $$"""
            { "type": "object", "properties": { "projects": { "type": "array", "items": {{ProjectSchema}} }, "lastProjectId": { "type": ["string", "null"] } },
              "required": ["projects"] }
            """,
            async call =>
            {
                call.Arguments.Only("includeArchived");
                var library = await dependencies.Projects.ListAsync(new ListProjectsRequestDto(), call.CancellationToken);
                var includeArchived = call.Arguments.Bool("includeArchived", false);
                var projects = library.Items.Where(project => includeArchived || project.ArchivedAt is null).ToArray();
                var structured = new JsonObject { ["projects"] = McpToolResult.Node(projects), ["lastProjectId"] = library.LastProjectId };
                return Result(structured, projects.Length == 0
                    ? "No projects."
                    : $"{projects.Length} projects: " + string.Join("; ", projects.Take(20).Select(project => $"{project.Name} ({project.Id})")) + (projects.Length > 20 ? "; …" : "."));
            });

        yield return McpToolDefinition.Create("get_project", "Get project", McpToolClass.Read, true,
            """
            Settings and summary of a project, as on its overview screen: dataset counts, the latest model versions with metrics and the active
            jobs. Prepares the project in the background without switching the window to it.
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "required": ["projectId"], "additionalProperties": false }""",
            $$"""
            { "type": "object", "properties": { "project": {{ProjectSchema}}, "overview": { "type": "object" } }, "required": ["project", "overview"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId");
                var (project, overview) = await ProjectSummaryAsync(call.Arguments.String("projectId"), call.CancellationToken);
                var structured = new JsonObject { ["project"] = McpToolResult.Node(project), ["overview"] = McpToolResult.Node(overview) };
                return Result(structured, $"Project {project.Name}: input {project.InputSize.Width}×{project.InputSize.Height}, base model {project.BaseModelId}, "
                    + $"{overview.ModelVersionCount} model versions, {overview.Datasets.Ready} datasets, {overview.ActiveJobs.Total} active jobs.");
            });

        yield return McpToolDefinition.Create("create_project", "Create project", McpToolClass.Write, false,
            """
            Creates a project. baseModelId is a base model of the catalog that is installed (see the base model tools); width and height are the
            model input in pixels and must be positive multiples of the model stride (32), for example 640×640. Returns the project with its id.
            """,
            """
            { "type": "object", "properties": {
                "name": { "type": "string", "description": "1-120 characters." },
                "description": { "type": "string", "default": "" },
                "baseModelId": { "type": "string", "description": "Catalog model ID, for example yolo26n." },
                "width": { "type": "integer", "minimum": 32 }, "height": { "type": "integer", "minimum": 32 } },
              "required": ["name", "baseModelId", "width", "height"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "project": {{ProjectSchema}} }, "required": ["project"] }""",
            async call =>
            {
                call.Arguments.Only("name", "description", "baseModelId", "width", "height");
                var request = new CreateProjectRequestDto(call.Arguments.String("name"), call.Arguments.OptionalString("description") ?? "",
                    call.Arguments.String("baseModelId"), new InputSizeDto(call.Arguments.Int("width", 1, 100_000), call.Arguments.Int("height", 1, 100_000)));
                var project = await dependencies.Projects.CreateAsync(request, call.CancellationToken);
                return Result(Wrap("project", project), $"Project {project.Name} created with id {project.Id}.");
            });

        yield return McpToolDefinition.Create("update_project", "Update project", McpToolClass.Write, true,
            """
            Changes the name, description, base model or model input of a project; omitted fields keep their values. Pass revision from
            get_project to make sure nobody changed the project meanwhile (projects.revision_conflict otherwise); without it the current revision is used.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "revision": { "type": "integer" },
                "name": { "type": "string" }, "description": { "type": "string" }, "baseModelId": { "type": "string" },
                "width": { "type": "integer", "minimum": 32 }, "height": { "type": "integer", "minimum": 32 } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "project": {{ProjectSchema}} }, "required": ["project"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "revision", "name", "description", "baseModelId", "width", "height");
                var current = await Core.Projects.GetAsync(arguments.String("projectId"), call.CancellationToken);
                var revision = arguments.Has("revision") ? arguments.Int("revision", 0, int.MaxValue) : current.Revision;
                var request = new UpdateProjectRequestDto(current.Id, revision, arguments.OptionalString("name") ?? current.Name,
                    arguments.OptionalString("description") ?? current.Description, arguments.OptionalString("baseModelId") ?? current.BaseModelId,
                    new InputSizeDto(arguments.Int("width", 1, 100_000, current.InputSize.Width), arguments.Int("height", 1, 100_000, current.InputSize.Height)));
                var project = await dependencies.Projects.UpdateAsync(request, call.CancellationToken);
                return Result(Wrap("project", project), $"Project {project.Name} saved (revision {project.Revision}).");
            });

        yield return McpToolDefinition.Create("archive_project", "Archive project", McpToolClass.Write, true,
            """
            Moves a project to the archive (archived=true, the default) or restores it (archived=false). An archived project is read-only and
            hidden from the active list; nothing is deleted. Repeating with the same value changes nothing.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "archived": { "type": "boolean", "default": true } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "project": {{ProjectSchema}} }, "required": ["project"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "archived");
                var archived = call.Arguments.Bool("archived", true);
                var project = await dependencies.Projects.SetArchivedAsync(new SetProjectArchivedRequestDto(call.Arguments.String("projectId"), archived),
                    call.CancellationToken);
                return Result(Wrap("project", project), archived ? $"Project {project.Name} is archived." : $"Project {project.Name} is restored from the archive.");
            });

        yield return McpToolDefinition.Create("open_project_in_app", "Show project in the window", McpToolClass.Write, true,
            """
            Opens a project in the application window and brings the window to the front, to show the user a result. Other tools work in the
            background and never switch the window, so call this only when the user should look.
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "required": ["projectId"], "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "project": {{ProjectSchema}} }, "required": ["project"] }""",
            async call =>
            {
                call.Arguments.Only("projectId");
                var project = await Core.Projects.GetAsync(call.Arguments.String("projectId"), call.CancellationToken);
                if (project.ArchivedAt is not null) throw new CoreException("projects.archived", $"Project {project.Id} is archived.");
                dependencies.OpenProjectInApp(project.Id);
                return Result(Wrap("project", ProjectResponseMapper.Map(project)), $"Project {project.Name} is shown in the application window.");
            });

        yield return McpToolDefinition.Create("delete_project", "Delete project", McpToolClass.Destructive, false,
            """
            Deletes a project with all its data from the computer. The user always confirms it in the application window (up to 2 minutes;
            mcp.confirmation_denied or mcp.confirmation_timeout otherwise). Running jobs block the deletion (projects.busy) unless cancelJobs=true,
            which cancels them first, as "Cancel jobs and delete" in the window. Prefer archive_project when the data may be needed.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "cancelJobs": { "type": "boolean", "default": false, "description": "Cancel running jobs and the live check before deleting." } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            """
            { "type": "object", "properties": { "projectId": { "type": "string" }, "cleanupPending": { "type": "boolean" } }, "required": ["projectId", "cleanupPending"] }
            """,
            async call =>
            {
                call.Arguments.Only("projectId", "cancelJobs");
                var projectId = call.Arguments.String("projectId");
                var cancelJobs = call.Arguments.Bool("cancelJobs", false);
                var review = await Core.Projects.ReviewDeletionAsync(projectId, call.CancellationToken);
                if (review.TransferActive) throw new CoreException("projects.transfer_active", "A project export is running.");
                if (review.Activities.Count > 0 && !cancelJobs) throw new CoreException("projects.busy", "Jobs of the project are running; pass cancelJobs.");
                await call.ConfirmAsync("delete_project", new Dictionary<string, string>
                {
                    ["name"] = review.Name,
                    ["jobs"] = review.Activities.Count.ToString(CultureInfo.InvariantCulture),
                });
                var result = await dependencies.Projects.DeleteAsync(new DeleteProjectRequestDto(projectId, review.Revision, cancelJobs), call.CancellationToken);
                return Result(McpToolResult.Object(result), result.CleanupPending
                    ? $"Project {review.Name} deleted; some locked files are removed after the next start."
                    : $"Project {review.Name} deleted.");
            });
    }

    /// <summary>The project and its overview, prepared in the background (shared with the resource maut://projects/{id}).</summary>
    internal async Task<(ProjectResponseDto Project, Controllers.Projects.Overview.ResponseDto.OverviewResponseDto Overview)> ProjectSummaryAsync(string projectId,
        CancellationToken cancellationToken)
    {
        var project = await PrepareAsync(projectId, cancellationToken);
        var overview = await dependencies.Overview.GetAsync(new GetOverviewRequestDto(project.Id), cancellationToken);
        return (ProjectResponseMapper.Map(project), overview);
    }
}
