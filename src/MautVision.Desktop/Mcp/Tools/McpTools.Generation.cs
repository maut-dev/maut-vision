using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Desktop.Controllers.Projects.Generation;
using MautVision.Desktop.Controllers.Projects.Generation.RequestDto;
using MautVision.Desktop.Controllers.Projects.Generation.ResponseDto;

namespace MautVision.Desktop.Mcp.Tools;

internal sealed partial class McpTools
{
    private const string RecipeSchema = """
        { "type": "object", "description": "Generation recipe (preset): id (recipeId), name, description, width×height of the frames, backgroundSetIds, classVariantIds (class-variant bindings objects are drawn from), variantWeights [{variantId, percentage}] (shares of project variants, sum 100), objectsPerImage, emptyProbability (0-1), scale, revision, archivedAt.",
          "properties": { "id": { "type": "string" }, "name": { "type": "string" }, "description": { "type": "string" },
            "width": { "type": "integer" }, "height": { "type": "integer" },
            "backgroundSetIds": { "type": "array", "items": { "type": "string" } }, "classVariantIds": { "type": "array", "items": { "type": "string" } },
            "variantWeights": { "type": "array", "items": { "type": "object", "properties": { "variantId": { "type": "string" }, "percentage": { "type": "number" } } } },
            "objectsPerImage": { "type": "integer" }, "emptyProbability": { "type": "number" }, "scale": { "type": "number" },
            "revision": { "type": "integer" }, "archivedAt": { "type": ["string", "null"] } },
          "required": ["id", "name", "width", "height", "backgroundSetIds", "classVariantIds", "variantWeights", "revision"] }
        """;

    /// <summary>The fields of the recipe form, shared by create_recipe and update_recipe.</summary>
    private const string RecipeFieldsSchema = """
        "name": { "type": "string", "description": "1-120 characters." },
        "description": { "type": "string", "description": "Up to 4000 characters." },
        "width": { "type": "integer", "minimum": 32, "description": "Frame width in pixels, a multiple of 32; default — the project input." },
        "height": { "type": "integer", "minimum": 32, "description": "Frame height in pixels, a multiple of 32; default — the project input." },
        "backgroundSetIds": { "type": "array", "minItems": 1, "items": { "type": "string" }, "description": "Background sets (list_background_sets) of exactly the frame size; backgrounds are drawn from their active backgrounds." },
        "classIds": { "type": "array", "items": { "type": "string" }, "description": "Classes to draw objects of; default — every class that has an available class-variant binding with images." },
        "classVariantIds": { "type": "array", "items": { "type": "string" }, "description": "Exact class-variant bindings (\"classVariantId\" of the catalog) to draw objects from; default — the available bindings with images of the chosen classes whose project variant has a share above 0." },
        "variantWeights": { "type": "array", "description": "Shares of project variants in percent, together 100 (\"Variant shares\"); an object first draws a variant by these shares, then a binding of it, then an image. Default — even shares over the variants of the chosen bindings.",
          "items": { "type": "object", "properties": { "variantId": { "type": "string" }, "percentage": { "type": "number", "minimum": 0, "maximum": 100 } }, "required": ["variantId", "percentage"], "additionalProperties": false } },
        "objectsPerImage": { "type": "integer", "minimum": 0, "description": "Objects placed on a frame (\"Objects per frame\"); objects that find no room without overlap are skipped. Default 1." },
        "emptyProbability": { "type": "number", "minimum": 0, "maximum": 1, "description": "Share of frames without objects (\"Empty frames\"), 0-1. Default 0.1." },
        "scale": { "type": "number", "exclusiveMinimum": 0, "description": "Object scale: an image is reduced to fit scale × the frame (never enlarged above its size). Default 1." }
        """;

    private static readonly string[] RecipeFields =
        ["name", "description", "width", "height", "backgroundSetIds", "classIds", "classVariantIds", "variantWeights", "objectsPerImage", "emptyProbability", "scale"];

    private GenerationController? generationController;

    private GenerationController Generation => generationController ??= new GenerationController(Core.Projects, Core.Jobs, importResources);

    private IEnumerable<McpToolDefinition> GenerationTools()
    {
        yield return McpToolDefinition.Create("list_recipes", "List generation recipes", McpToolClass.Read, true,
            """
            Generation recipes (presets) of a project with all parameters, archived ones included (archivedAt). A recipeId is used by get_recipe,
            update_recipe, start_generation and as a view target: view { target: { generationSample: recipeId, sampleIndex: 0-3, seed } } renders a
            sample frame of the recipe with its boxes without saving anything.
            """,
            $$"""{ "type": "object", "properties": { "projectId": {{ProjectIdSchema}} }, "required": ["projectId"], "additionalProperties": false }""",
            $$"""{ "type": "object", "properties": { "recipes": { "type": "array", "items": {{RecipeSchema}} } }, "required": ["recipes"] }""",
            async call =>
            {
                call.Arguments.Only("projectId");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var recipes = await Generation.ListRecipesAsync(new ListGenerationRecipesRequestDto(projectId), call.CancellationToken);
                return Result(Wrap("recipes", recipes), recipes.Length == 0
                    ? "No generation recipes."
                    : $"{recipes.Length} recipes: " + string.Join("; ", recipes.Take(20).Select(recipe => $"{recipe.Name} ({recipe.Id:D})")) + ".");
            });

        yield return McpToolDefinition.Create("get_recipe", "Get generation recipe", McpToolClass.Read, true,
            "All parameters of a generation recipe. View a sample frame with view { target: { generationSample: recipeId } }.",
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "recipeId": { "type": "string" } },
              "required": ["projectId", "recipeId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "recipe": {{RecipeSchema}} }, "required": ["recipe"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "recipeId");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var recipe = await Generation.GetRecipeAsync(new GetGenerationRecipeRequestDto(projectId, call.Arguments.Guid("recipeId")), call.CancellationToken);
                return Result(Wrap("recipe", recipe), Describe(recipe));
            });

        yield return McpToolDefinition.Create("create_recipe", "Create generation recipe", McpToolClass.Write, false,
            """
            Creates a generation recipe, the same form as "New preset" in the window: objects cut from class images (transparent PNG of
            class-variant bindings) are placed onto backgrounds of the chosen sets and annotated at once. Only name and backgroundSetIds are
            required; the classes, bindings and variant shares default like the window (every available binding with images, even shares).
            An invalid field is generation.invalid_recipe with details.field. Check the result with view { generationSample: recipeId }, then
            start_generation.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, {{RecipeFieldsSchema}} },
              "required": ["projectId", "name", "backgroundSetIds"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "recipe": {{RecipeSchema}} }, "required": ["recipe"] }""",
            async call =>
            {
                call.Arguments.Only(["projectId", .. RecipeFields]);
                var projectId = call.Arguments.String("projectId");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var form = await RecipeFormAsync(project, call.Arguments, null, call.CancellationToken);
                var recipe = await Generation.CreateRecipeAsync(new CreateGenerationRecipeRequestDto(projectId, form), call.CancellationToken);
                return Result(Wrap("recipe", recipe), $"Recipe {recipe.Name} created with id {recipe.Id:D}. " + Describe(recipe));
            });

        yield return McpToolDefinition.Create("update_recipe", "Update generation recipe", McpToolClass.Write, true,
            """
            Changes a generation recipe; omitted fields keep their values. Giving classIds, classVariantIds or variantWeights recomputes the
            sources with the same defaults as create_recipe. archived=true archives the recipe (read-only, cannot run), archived=false restores
            it. Pass revision from get_recipe to detect concurrent changes (generation.revision_conflict).
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "recipeId": { "type": "string" }, "revision": { "type": "integer" },
                "archived": { "type": "boolean" }, {{RecipeFieldsSchema}} },
              "required": ["projectId", "recipeId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { "recipe": {{RecipeSchema}} }, "required": ["recipe"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only(["projectId", "recipeId", "revision", "archived", .. RecipeFields]);
                var projectId = arguments.String("projectId");
                var recipeId = arguments.Guid("recipeId");
                var project = await PrepareAsync(projectId, call.CancellationToken);
                var recipe = await Generation.GetRecipeAsync(new GetGenerationRecipeRequestDto(projectId, recipeId), call.CancellationToken);
                var revision = Revision(arguments, recipe.Revision);
                bool? archived = arguments.Has("archived") ? arguments.Bool("archived", false) : null;
                if (archived == false && recipe.ArchivedAt is not null)
                {
                    recipe = await Generation.SetRecipeArchivedAsync(new SetGenerationRecipeArchivedRequestDto(projectId, recipeId, false, revision),
                        call.CancellationToken);
                    revision = recipe.Revision;
                }
                if (RecipeFields.Any(arguments.Has))
                {
                    var form = await RecipeFormAsync(project, arguments, recipe, call.CancellationToken);
                    recipe = await Generation.UpdateRecipeAsync(new UpdateGenerationRecipeRequestDto(projectId, recipeId, form, revision), call.CancellationToken);
                    revision = recipe.Revision;
                }
                if (archived == true && recipe.ArchivedAt is null)
                {
                    recipe = await Generation.SetRecipeArchivedAsync(new SetGenerationRecipeArchivedRequestDto(projectId, recipeId, true, revision),
                        call.CancellationToken);
                }
                return Result(Wrap("recipe", recipe), $"Recipe {recipe.Name} saved (revision {recipe.Revision}{(recipe.ArchivedAt is null ? "" : ", archived")}).");
            });

        yield return McpToolDefinition.Create("delete_recipe", "Delete generation recipe", McpToolClass.Destructive, false,
            """
            Permanently deletes a generation recipe. Runs keep their own copy of it and their images stay. An unfinished run of the recipe
            blocks the deletion (generation.deletion_blocked). Pass revision from get_recipe or omit it for the current one.
            """,
            $$"""
            { "type": "object", "properties": { "projectId": {{ProjectIdSchema}}, "recipeId": { "type": "string" }, "revision": { "type": "integer" } },
              "required": ["projectId", "recipeId"], "additionalProperties": false }
            """,
            """{ "type": "object", "properties": { "recipeId": { "type": "string" } }, "required": ["recipeId"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "recipeId", "revision");
                var projectId = call.Arguments.String("projectId");
                var recipeId = call.Arguments.Guid("recipeId");
                await PrepareAsync(projectId, call.CancellationToken);
                var recipe = await Generation.GetRecipeAsync(new GetGenerationRecipeRequestDto(projectId, recipeId), call.CancellationToken);
                var revision = Revision(call.Arguments, recipe.Revision);
                var result = await Generation.DeleteRecipeAsync(new DeleteGenerationRecipeRequestDto(projectId, recipeId, revision), call.CancellationToken);
                return Result(McpToolResult.Object(result), $"Recipe {recipe.Name} deleted.");
            });

        yield return McpToolDefinition.Create("start_generation", "Start generation", McpToolClass.Write, false,
            """
            Generates count annotated frames with a recipe as a job, like "Generate N images" in the window. The frames go to a new image group
            (run.outputGroupId, filled while the job runs); they are reviewed images of that group, so plan_dataset can take the group at once.
            The seed (0-4294967295, random by default) makes the run reproducible: the same seed gives the same frames as view
            { generationSample: recipeId, seed }. Returns { jobId, status, job, run } at once; jobId is also the runId of list_generation_runs.
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}}, "recipeId": { "type": "string" },
                "count": { "type": "integer", "minimum": 1, "maximum": 1000000, "default": 100, "description": "Frames to generate." },
                "seed": { "type": "integer", "minimum": 0, "maximum": 4294967295 },
                "revision": { "type": "integer", "description": "Recipe revision; omitted — the current one." },
                "operationId": { "type": "string", "description": "Optional key of this action: repeating a call with the same operationId returns the same run." },
                {{WaitSecondsSchema}} },
              "required": ["projectId", "recipeId"], "additionalProperties": false }
            """,
            $$"""{ "type": "object", "properties": { {{StartedJobSchema}}, "run": { "type": "object" } }, "required": ["jobId", "status", "job", "run"] }""",
            async call =>
            {
                var arguments = call.Arguments;
                arguments.Only("projectId", "recipeId", "count", "seed", "revision", "operationId", "waitSeconds");
                var projectId = arguments.String("projectId");
                var recipeId = arguments.Guid("recipeId");
                await PrepareAsync(projectId, call.CancellationToken);
                var recipe = await Generation.GetRecipeAsync(new GetGenerationRecipeRequestDto(projectId, recipeId), call.CancellationToken);
                var revision = Revision(arguments, recipe.Revision);
                var seed = arguments.Has("seed") ? (long)arguments.Double("seed", 0, uint.MaxValue) : RandomNumberGenerator.GetInt32(int.MaxValue);
                var run = await Generation.StartAsync(new StartGenerationRunRequestDto(projectId, recipeId, revision, seed,
                    arguments.Int("count", 1, 1_000_000, 100), OperationId(arguments)), call.CancellationToken);
                var result = await StartedJobAsync(projectId, run.Job.Id, arguments, call, "Generation");
                run = await Generation.GetRunAsync(new GetGenerationRunRequestDto(projectId, run.Job.Id), call.CancellationToken);
                result.Structured["run"] = McpToolResult.Node(run);
                return result with { Text = result.Text + (run.OutputGroupId is { } group ? $" Output image group {group:D}." : "") };
            });

        yield return McpToolDefinition.Create("list_generation_runs", "List generation runs", McpToolClass.Read, true,
            """
            Generation runs of a project, newest first: the job snapshot, the copy of the recipe the run used, seed, requested and produced
            frames, frames with missing objects and outputGroupId — the image group with the generated frames (use it in plan_dataset groupIds).
            """,
            $$"""
            { "type": "object", "properties": {
                "projectId": {{ProjectIdSchema}},
                "limit": { "type": "integer", "minimum": 1, "maximum": 200, "default": 20 },
                "before": { "type": "string", "description": "runId of the last run of the previous page." } },
              "required": ["projectId"], "additionalProperties": false }
            """,
            """{ "type": "object", "properties": { "runs": { "type": "array", "items": { "type": "object" } }, "total": { "type": "integer" } }, "required": ["runs", "total"] }""",
            async call =>
            {
                call.Arguments.Only("projectId", "limit", "before");
                var projectId = call.Arguments.String("projectId");
                await PrepareAsync(projectId, call.CancellationToken);
                var page = await Generation.ListRunsAsync(new ListGenerationRunsRequestDto(projectId, call.Arguments.Int("limit", 1, 200, 20),
                    call.Arguments.OptionalGuid("before")), call.CancellationToken);
                var structured = new JsonObject { ["runs"] = McpToolResult.Node(page.Items), ["total"] = page.Total };
                return Result(structured, $"{page.Total} generation runs; " + string.Join("; ", page.Items.Take(10).Select(run =>
                    $"{run.Job.Id:D} {run.Job.Status} {run.ProducedCount}/{run.RequestedCount}" + (run.OutputGroupId is { } group ? $" → group {group:D}" : ""))) + ".");
            });
    }

    private static string Describe(GenerationRecipeResponseDto recipe)
        => $"Recipe {recipe.Name}: {recipe.Width}×{recipe.Height}, {recipe.BackgroundSetIds.Length} background sets, {recipe.ClassVariantIds.Length} bindings, "
            + $"{recipe.ObjectsPerImage} objects per frame, {recipe.EmptyProbability:0.##} empty, scale {recipe.Scale:0.##}, revision {recipe.Revision}"
            + (recipe.ArchivedAt is null ? "." : ", archived.");

    /// <summary>
    /// The recipe form of the window from the arguments: omitted fields keep the values of <paramref name="current"/> (or the defaults of a
    /// new preset); sources follow the rules of the form: bindings are the available ones with images whose project variant has a share.
    /// </summary>
    private static async Task<GenerationRecipeFormDto> RecipeFormAsync(IProject project, McpArguments arguments, GenerationRecipeResponseDto? current,
        CancellationToken cancellationToken)
    {
        var name = arguments.OptionalString("name") ?? current?.Name ?? throw McpToolException.InvalidArgument("name", "required");
        var description = arguments.OptionalString("description") ?? current?.Description ?? "";
        var width = arguments.Int("width", 1, 100_000, current?.Width ?? project.InputSize.Width);
        var height = arguments.Int("height", 1, 100_000, current?.Height ?? project.InputSize.Height);
        var sets = arguments.Has("backgroundSetIds") ? GuidList(arguments, "backgroundSetIds", 1, 1000) : current?.BackgroundSetIds
            ?? throw McpToolException.InvalidArgument("backgroundSetIds", "required");
        Guid[] bindings;
        GenerationVariantWeightDto[] weights;
        if (current is not null && !arguments.Has("classIds") && !arguments.Has("classVariantIds") && !arguments.Has("variantWeights"))
        {
            bindings = current.ClassVariantIds;
            weights = current.VariantWeights;
        }
        else
        {
            var workspace = await project.Classes.GetWorkspaceAsync(cancellationToken);
            (bindings, weights) = RecipeSources(workspace, (arguments.Has("classIds") ? GuidList(arguments, "classIds", 0, 100_000) : null), (arguments.Has("classVariantIds") ? GuidList(arguments, "classVariantIds", 0, 100_000) : null), WeightsArgument(arguments));
        }
        return new GenerationRecipeFormDto(name, description, width, height, sets, bindings, weights,
            arguments.Int("objectsPerImage", 0, 10_000, current?.ObjectsPerImage ?? 1),
            arguments.Double("emptyProbability", 0, 1, current?.EmptyProbability ?? 0.1),
            arguments.Double("scale", double.Epsilon, 1_000, current?.Scale ?? 1));
    }

    private static GenerationVariantWeightDto[]? WeightsArgument(McpArguments arguments)
    {
        if (!arguments.Has("variantWeights")) return null;
        return arguments.Objects("variantWeights", 1, 1000).Select(item =>
        {
            item.Only("variantId", "percentage");
            return new GenerationVariantWeightDto(item.Guid("variantId"), item.Double("percentage", 0, 100));
        }).ToArray();
    }

    /// <summary>
    /// Bindings and shares like the form: eligible bindings are available and have images; explicit bindings win; shares default to even
    /// whole percents over the variants of the bindings (the first ones get the remainder), in catalog order.
    /// </summary>
    internal static (Guid[] Bindings, GenerationVariantWeightDto[] Weights) RecipeSources(CatalogWorkspace workspace, Guid[]? classIds, Guid[]? classVariantIds,
        GenerationVariantWeightDto[]? weights)
    {
        var eligible = workspace.ClassVariants.Where(binding => binding.Available && binding.AssetCount > 0
            && (classIds is null || classIds.Contains(binding.ClassId))).ToArray();
        Guid[] bindings;
        if (classVariantIds is not null) bindings = classVariantIds;
        else if (weights is not null)
        {
            var weighted = weights.Where(weight => weight.Percentage > 0).Select(weight => weight.VariantId).ToHashSet();
            bindings = eligible.Where(binding => weighted.Contains(binding.VariantId)).Select(binding => binding.Id).ToArray();
        }
        else bindings = eligible.Select(binding => binding.Id).ToArray();
        if (weights is null)
        {
            var variantOf = workspace.ClassVariants.ToDictionary(binding => binding.Id, binding => binding.VariantId);
            var used = bindings.Where(variantOf.ContainsKey).Select(id => variantOf[id]).ToHashSet();
            var variants = workspace.Variants.Where(variant => used.Contains(variant.Id)).Select(variant => variant.Id).ToArray();
            weights = variants.Select((variantId, index) => new GenerationVariantWeightDto(variantId,
                100 / variants.Length + (index < 100 % variants.Length ? 1 : 0))).ToArray();
        }
        return (bindings, weights);
    }
}
