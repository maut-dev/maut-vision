using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

namespace MautVision.Desktop;

internal static partial class SmokeRunner
{
    /// <summary>
    /// Generation through MCP: a recipe with the defaults of the window (the binding and an even share of its variant), invalid shares,
    /// changes with the revision rules, a sample frame through view, a run of 16 frames into a new group, the run list, a set blocked by the
    /// recipe and an unknown recipe. Returns the recipe and the output group.
    /// </summary>
    private static async Task<(string RecipeId, string GroupId)> CheckMcpGenerationAsync(McpClient client, McpChain chain, JsonArray checks, CancellationToken token)
    {
        var projectId = chain.ProjectId;
        var sets = new[] { chain.ImageSetId, chain.VideoSetId };
        Error(await CallAsync(client, "create_recipe", new
        {
            projectId, name = "MCP плохой", backgroundSetIds = sets, classIds = new[] { chain.ClassId },
            variantWeights = new[] { new { variantId = chain.VariantId, percentage = 50.0 } },
        }, token), "generation.invalid_recipe", null);
        var created = Ok(await CallAsync(client, "create_recipe", new
        {
            projectId, name = "MCP пресет", backgroundSetIds = sets, classIds = new[] { chain.ClassId }, objectsPerImage = 2, emptyProbability = 0.0, scale = 0.5,
        }, token), "create_recipe").GetProperty("recipe");
        var recipeId = created.GetProperty("id").GetString()!;
        var bindings = created.GetProperty("classVariantIds").EnumerateArray().Select(item => item.GetString()).ToArray();
        var weights = created.GetProperty("variantWeights");
        if (bindings.Length != 1 || bindings[0] != chain.BindingId.ToString("D") || weights.GetArrayLength() != 1
            || weights[0].GetProperty("variantId").GetString() != chain.VariantId.ToString("D") || weights[0].GetProperty("percentage").GetDouble() != 100)
            throw new InvalidOperationException("create_recipe defaults: " + created);
        var updated = Ok(await CallAsync(client, "update_recipe", new { projectId, recipeId, objectsPerImage = 1, description = "From an AI client" }, token),
            "update_recipe").GetProperty("recipe");
        if (updated.GetProperty("objectsPerImage").GetInt32() != 1 || updated.GetProperty("revision").GetInt64() <= created.GetProperty("revision").GetInt64()
            || updated.GetProperty("classVariantIds").GetArrayLength() != 1 || updated.GetProperty("scale").GetDouble() != 0.5)
            throw new InvalidOperationException("update_recipe: " + updated);
        Error(await CallAsync(client, "update_recipe", new { projectId, recipeId, name = "Stale", revision = created.GetProperty("revision").GetInt64() }, token),
            "generation.revision_conflict", null);
        var fetched = Ok(await CallAsync(client, "get_recipe", new { projectId, recipeId }, token), "get_recipe").GetProperty("recipe");
        var listed = Ok(await CallAsync(client, "list_recipes", new { projectId }, token), "list_recipes").GetProperty("recipes");
        if (fetched.GetProperty("description").GetString() != "From an AI client" || !listed.EnumerateArray().Any(item => item.GetProperty("id").GetString() == recipeId))
            throw new InvalidOperationException("get_recipe/list_recipes: " + fetched);
        var sample = await CallAsync(client, "view", new { projectId, target = new { generationSample = recipeId, sampleIndex = 1, seed = 7 } }, token);
        var sampled = Ok(sample, "view");
        using (var picture = DecodeMcpImage(sample))
        {
            if (sampled.GetProperty("kind").GetString() != "generationSample" || sampled.GetProperty("boxes").GetArrayLength() < 1 || picture.Width < 64)
                throw new InvalidOperationException("view generationSample: " + sampled);
        }
        checks.Add("create_recipe fills the binding and an even variant share like the window, refuses shares not summing to 100; update_recipe keeps omitted fields and checks the revision; a recipeId is a view target of sample frames");

        Error(await CallAsync(client, "start_generation", new { projectId, recipeId = Guid.NewGuid(), count = 2 }, token), "generation.unavailable", null);
        var started = Ok(await CallAsync(client, "start_generation", new { projectId, recipeId, count = 16, seed = 7 }, token), "start_generation");
        var runId = started.GetProperty("jobId").GetString()!;
        if (started.GetProperty("run").GetProperty("job").GetProperty("id").GetString() != runId) throw new InvalidOperationException("start_generation: " + started);
        var job = await McpFinishedJobAsync(client, projectId, runId, token);
        var result = job.GetProperty("result");
        var groupId = result.GetProperty("outputGroupId").GetString()!;
        if (result.GetProperty("producedCount").GetInt32() != 16) throw new InvalidOperationException("Generation result: " + result);
        var runs = Ok(await CallAsync(client, "list_generation_runs", new { projectId, limit = 5 }, token), "list_generation_runs");
        var run = runs.GetProperty("runs").EnumerateArray().Single(item => item.GetProperty("job").GetProperty("id").GetString() == runId);
        if (run.GetProperty("outputGroupId").GetString() != groupId || run.GetProperty("seed").GetInt64() != 7) throw new InvalidOperationException("list_generation_runs: " + run);
        var blocked = Error(await CallAsync(client, "delete_background_set", new { projectId, setId = chain.ImageSetId }, token), "backgrounds.deletion_blocked", null);
        if (blocked.GetProperty("details").GetProperty("blockers").GetString()?.Contains("generation.preset_reference", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Blocked set deletion: " + blocked);
        checks.Add("start_generation runs 16 frames into a new image group followed with wait_job; list_generation_runs shows the run; a set used by the recipe cannot be deleted (blockers in details)");
        return (recipeId, groupId);
    }
}
