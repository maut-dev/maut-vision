using System.Globalization;
using System.Text.Json;
using MautVision.Core.Interfaces;
using MautVision.Core.Interfaces.Jobs;
using MautVision.Core.Interfaces.Projects;
using MautVision.Core.Interfaces.Projects.Catalog;
using MautVision.Core.Interfaces.Projects.Generation;
using MautVision.Core.Jobs;
using MautVision.Core.Projects.Backgrounds;
using MautVision.Core.Projects.Catalog;
using MautVision.Core.Projects.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using static MautVision.Core.Projects.Catalog.CatalogSql;

namespace MautVision.Core.Projects.Generation;

/// <summary>Durable position of a run: the contiguous prefix of published frames and how many of them miss objects.</summary>
internal sealed record GenerationCheckpoint(int Produced, int MissingFrames);

/// <summary>What a run prepared: its output group and the manifest of its snapshot (null until prepared); Released — files are gone.</summary>
internal sealed record PreparedRun(Guid Id, Guid? OutputGroupId, GenerationManifest? Manifest, bool Released);

/// <summary>
/// Generation presets and runs of one project: tables generation_recipes and generation_runs; a run itself is the job
/// generation.run. The manager reads the catalog and the backgrounds through their managers (one way: they do not know about
/// generation) to check sources, build a run manifest and draw sample frames; the run is coordinated by
/// <see cref="GenerationRunHandler"/>.
/// </summary>
internal sealed class GenerationManager : IGenerationManager
{
    internal const int MaxNameLength = 120;
    internal const int MaxDescriptionLength = 4000;
    internal const int MaxPage = 200;
    internal const int MaxSamples = 4;
    internal const int CanvasStep = 32;
    internal const double WeightTolerance = 1e-6;
    private const string RecipeColumns = "id, name, description, width, height, background_set_ids, class_variant_ids, variant_weights, objects_per_image, "
        + "empty_probability, scale, revision, created_at, updated_at, archived_at";

    private static readonly JsonSerializerOptions Json = JobsManager.Json;

    private readonly string projectId;
    private readonly ProjectStore store;
    private readonly Func<bool> isOpen;
    private readonly Func<InputSize> projectInput;
    private readonly Action<GenerationChangedEventArgs> changed;
    private readonly ClassesManager classes;
    private readonly BackgroundsManager backgrounds;
    private readonly ILogger logger;

    internal GenerationManager(string projectId, ProjectStore store, Func<bool> isOpen, Func<InputSize> projectInput, Action<GenerationChangedEventArgs> changed,
        ClassesManager classes, BackgroundsManager backgrounds, ILogger logger)
    {
        this.projectId = projectId;
        this.store = store;
        this.isOpen = isOpen;
        this.projectInput = projectInput;
        this.changed = changed;
        this.classes = classes;
        this.backgrounds = backgrounds;
        this.logger = logger;
    }

    // ---- Recipes ----

    public async Task<IReadOnlyList<GenerationRecipe>> ListRecipesAsync(CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT {RecipeColumns} FROM generation_recipes ORDER BY created_at, id;");
            return (IReadOnlyList<GenerationRecipe>)await ListAsync(command, ReadRecipe, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationRecipe> GetRecipeAsync(Guid recipeId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync((connection, token) => FindRecipeAsync(connection, null, recipeId, token), cancellationToken).ConfigureAwait(false)
            ?? throw Unavailable(recipeId);
    }

    public async Task<GenerationRecipe> CreateRecipeAsync(GenerationRecipeInput input, CancellationToken cancellationToken = default)
    {
        var recipe = Validate(input);
        RequireOpen();
        await CheckReferencesAsync(recipe, cancellationToken).ConfigureAwait(false);
        var created = await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, $"""
                INSERT INTO generation_recipes ({RecipeColumns})
                VALUES ($id, $name, $description, $width, $height, $sets, $bindings, $weights, $objects, $empty, $scale, 1, $now, $now, NULL)
                RETURNING {RecipeColumns};
                """);
            command.Parameters.AddWithValue("$id", Key(Guid.NewGuid()));
            command.Parameters.AddWithValue("$now", Time(Now()));
            BindRecipe(command, recipe);
            return (await SingleOrDefaultAsync(command, ReadRecipe, token).ConfigureAwait(false))!;
        }, cancellationToken).ConfigureAwait(false);
        Raise(recipes: [new(created.Id, created.Revision)]);
        return created;
    }

    public async Task<GenerationRecipe> UpdateRecipeAsync(Guid recipeId, GenerationRecipeInput input, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var recipe = Validate(input);
        RequireOpen();
        await CheckReferencesAsync(recipe, cancellationToken).ConfigureAwait(false);
        return await ChangeRecipeAsync(recipeId, expectedRevision, current =>
        {
            if (current.ArchivedAt is not null) throw new CoreException("generation.recipe_archived", $"Recipe {recipeId} is archived; restore it first.");
            if (Same(current.ToInput(), recipe)) return null;
            return ("name = $name, description = $description, width = $width, height = $height, background_set_ids = $sets, class_variant_ids = $bindings, "
                + "variant_weights = $weights, objects_per_image = $objects, empty_probability = $empty, scale = $scale", command => BindRecipe(command, recipe));
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<GenerationRecipe> SetRecipeArchivedAsync(Guid recipeId, bool archived, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return ChangeRecipeAsync(recipeId, expectedRevision, current => (current.ArchivedAt is not null) == archived
            ? null
            : ("archived_at = $archived", command => command.Parameters.AddWithValue("$archived", archived ? Time(Now()) : DBNull.Value)),
            cancellationToken);
    }

    public async Task DeleteRecipeAsync(Guid recipeId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindRecipeAsync(connection, transaction, recipeId, token).ConfigureAwait(false) ?? throw Unavailable(recipeId);
            if (current.Revision != expectedRevision)
                throw RevisionConflict($"Recipe {recipeId} has revision {current.Revision}, expected {expectedRevision}.");
            // Served by the partial index of unfinished jobs; a run in the queue still needs its recipe to be listed with it.
            await using (var active = Command(connection, transaction, """
                SELECT count(*) FROM jobs WHERE status IN ('queued', 'running', 'blocked') AND kind = 'generation.run'
                    AND json_extract(input, '$.recipe.id') = $id;
                """))
            {
                active.Parameters.AddWithValue("$id", Key(recipeId));
                if (await ScalarAsync(active, token).ConfigureAwait(false) > 0)
                    throw new CoreException("generation.deletion_blocked", $"Recipe {recipeId} has an unfinished run.");
            }
            await using var delete = Command(connection, transaction, "DELETE FROM generation_recipes WHERE id = $id;");
            delete.Parameters.AddWithValue("$id", Key(recipeId));
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Raise(recipes: [new(recipeId, null)]);
        logger.LogInformation("Project {ProjectId}: generation recipe {RecipeId} deleted.", projectId, recipeId);
    }

    // ---- Runs ----

    public async Task<GenerationRunPage> ListRunsAsync(int limit = 50, Guid? before = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPage);
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var jobs = await JobsTable.ListStoredByKindAsync(connection, projectId, GenerationRunHandler.Kind, limit, before, token).ConfigureAwait(false);
            var prepared = await ReadPreparedAsync(connection, jobs.Select(job => job.Info.Id).ToArray(), token).ConfigureAwait(false);
            var total = await JobsTable.CountByKindAsync(connection, GenerationRunHandler.Kind, token).ConfigureAwait(false);
            var runs = jobs.Select(job => ToRun(job, prepared.GetValueOrDefault(job.Info.Id))).OfType<GenerationRun>().ToArray();
            return new GenerationRunPage(runs, total);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationRun> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        return await store.ReadAsync(async (connection, token) =>
        {
            var job = await JobsTable.GetStoredAsync(connection, null, projectId, runId, token).ConfigureAwait(false);
            if (job is null || job.Info.Kind != GenerationRunHandler.Kind) throw Unavailable(runId);
            var prepared = await ReadPreparedAsync(connection, [runId], token).ConfigureAwait(false);
            return ToRun(job, prepared.GetValueOrDefault(runId)) ?? throw Unavailable(runId);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GenerationRunInput> PrepareRunAsync(Guid recipeId, long expectedRevision, uint seed, int count, string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationId.Length, JobsManager.MaxOperationIdLength, nameof(operationId));
        RequireOpen();
        var (existing, deleted, recipe) = await store.ReadAsync(async (connection, token) =>
        {
            var job = await JobsTable.FindStoredByOperationAsync(connection, null, projectId, operationId, token).ConfigureAwait(false);
            await using var tombstone = Command(connection, null, "SELECT EXISTS (SELECT 1 FROM generation_runs WHERE operation_id = $operation AND deleted_at IS NOT NULL);");
            tombstone.Parameters.AddWithValue("$operation", operationId);
            var removed = await ScalarAsync(tombstone, token).ConfigureAwait(false) == 1;
            return (job, removed, await FindRecipeAsync(connection, null, recipeId, token).ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // A repeated request of the same operation returns the run it started, whatever changed since.
            if (existing.Info.Kind != GenerationRunHandler.Kind)
                throw new ArgumentException($"Operation {operationId} already belongs to a job of kind {existing.Info.Kind}.", nameof(operationId));
            return ParseInput(existing.Input) ?? throw Unavailable(existing.Info.Id);
        }
        if (deleted) throw new CoreException("generation.deleted_operation", $"Operation {operationId} belongs to a deleted generation run.");
        if (count < 1) throw new CoreException("generation.count_invalid", $"A run produces at least one image, not {count}.");
        if (recipe is null || recipe.ArchivedAt is not null) throw Unavailable(recipeId);
        if (recipe.Revision != expectedRevision) throw RevisionConflict($"Recipe {recipeId} has revision {recipe.Revision}, expected {expectedRevision}.");
        await GenerationSources.LoadAsync(recipe, projectInput(), classes, backgrounds, cancellationToken).ConfigureAwait(false);
        return new GenerationRunInput(recipe, seed, count, OutputGroupName(recipe.Name, DateTime.Now));
    }

    public async Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        RequireOpen();
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            var job = await JobsTable.GetStoredAsync(connection, transaction, projectId, runId, token).ConfigureAwait(false);
            if (job is null || job.Info.Kind != GenerationRunHandler.Kind) throw Unavailable(runId);
            if (!await JobsTable.DeleteFinishedAsync(connection, transaction, runId, token).ConfigureAwait(false))
                throw new CoreException("generation.run_active", $"Generation run {runId} is not finished.");
            // The row stays as a tombstone of the operation; the manifest is not needed any more.
            await using var tombstone = Command(connection, transaction, """
                INSERT INTO generation_runs (id, operation_id, created_at, released_at, deleted_at) VALUES ($id, $operation, $now, $now, $now)
                ON CONFLICT (id) DO UPDATE SET sources = NULL, deleted_at = $now;
                """);
            tombstone.Parameters.AddWithValue("$id", Key(runId));
            tombstone.Parameters.AddWithValue("$operation", job.Info.OperationId);
            tombstone.Parameters.AddWithValue("$now", Time(Now()));
            await tombstone.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        await ReleaseSnapshotAsync(runId, CancellationToken.None).ConfigureAwait(false);
        Raise(runs: [runId]);
        logger.LogInformation("Project {ProjectId}: generation run {RunId} deleted; its images stay.", projectId, runId);
    }

    public async Task<GenerationRepeatDraft> GetRepeatDraftAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        // The name stays the name of the run's preset: the UI adds " · repeat" in the language of the user.
        return new GenerationRepeatDraft(run.Recipe.ToInput(), run.Seed, run.RequestedCount, runId);
    }

    public async Task<IReadOnlyList<GenerationSample>> PreviewSamplesAsync(GenerationRecipeInput input, uint seed, int count = MaxSamples,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxSamples);
        var validated = Validate(input);
        RequireOpen();
        await CheckReferencesAsync(validated, cancellationToken).ConfigureAwait(false);
        var now = Now();
        var recipe = new GenerationRecipe(Guid.Empty, validated.Name, validated.Description, validated.Width, validated.Height, validated.BackgroundSetIds,
            validated.ClassVariantIds, validated.VariantWeights, validated.ObjectsPerImage, validated.EmptyProbability, validated.Scale, 0, now, now, null);
        var manifest = await GenerationSources.LoadAsync(recipe, projectInput(), classes, backgrounds, cancellationToken).ConfigureAwait(false);
        var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        // Samples read the material files themselves: nothing is fixed until a run starts.
        using var renderer = new GenerationRenderer(manifest.Layout(recipe), seed,
            (binding, asset) => files.GetPath(ClassAsset.ResourceKind, manifest.Bindings[binding].Assets[asset].Id, manifest.Bindings[binding].Assets[asset].Extension),
            index => files.GetPath(Interfaces.Projects.Backgrounds.BackgroundItem.ResourceKind, manifest.Backgrounds[index], ".png"));
        var samples = new GenerationSample[count];
        await Parallel.ForAsync(0, count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = count }, (index, _) =>
        {
            using var frame = renderer.Render(index);
            samples[index] = new GenerationSample(index, manifest.Backgrounds[frame.Plan.Background],
                frame.Plan.Objects.Select(item => new GenerationSampleObject(manifest.Bindings[item.Binding].ClassId, manifest.Bindings[item.Binding].Id,
                    renderer.Box(item))).ToArray(), frame.Plan.Missing, frame.Png.ToArray(), recipe.Width, recipe.Height);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return samples;
    }

    // ---- Run support ----

    /// <summary>Not gated on <c>isOpen</c>: a recovered run starts while the project is being opened.</summary>
    internal Task<MaterialFiles> GetFilesAsync(CancellationToken cancellationToken) => store.GetFilesAsync(cancellationToken);

    internal InputSize ProjectInput => projectInput();

    internal Task<GenerationManifest> LoadManifestAsync(GenerationRecipe recipe, CancellationToken cancellationToken)
        => GenerationSources.LoadAsync(recipe, projectInput(), classes, backgrounds, cancellationToken);

    /// <summary>The run row of a job: created on its first start, found again when it resumes.</summary>
    internal async Task<PreparedRun> RegisterRunAsync(Guid runId, string operationId, CancellationToken cancellationToken)
        => await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using (var insert = Command(connection, transaction,
                "INSERT INTO generation_runs (id, operation_id, created_at) VALUES ($id, $operation, $now) ON CONFLICT (id) DO NOTHING;"))
            {
                insert.Parameters.AddWithValue("$id", Key(runId));
                insert.Parameters.AddWithValue("$operation", operationId);
                insert.Parameters.AddWithValue("$now", Time(Now()));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return (await ReadPreparedAsync(connection, [runId], token, transaction, manifest: true).ConfigureAwait(false))[runId];
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>Stores the output group and the manifest once the snapshot and the group exist.</summary>
    internal async Task SavePreparedAsync(Guid runId, Guid groupId, GenerationManifest manifest, CancellationToken cancellationToken)
    {
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, "UPDATE generation_runs SET output_group_id = $group, sources = $sources WHERE id = $id;");
            command.Parameters.AddWithValue("$id", Key(runId));
            command.Parameters.AddWithValue("$group", Key(groupId));
            command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(manifest, Json));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        Raise(runs: [runId]);
    }

    /// <summary>Removes the run's snapshot files and marks them released; repeating it changes nothing.</summary>
    internal async Task ReleaseSnapshotAsync(Guid runId, CancellationToken cancellationToken)
    {
        var files = await store.GetFilesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GenerationSnapshot.Release(files, runId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A file still open somewhere keeps the folder; the next open of the project tries again.
            logger.LogWarning(error, "Project {ProjectId}: snapshot of generation run {RunId} was not removed.", projectId, runId);
            return;
        }
        await store.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = Command(connection, transaction, "UPDATE generation_runs SET released_at = $now WHERE id = $id AND released_at IS NULL;");
            command.Parameters.AddWithValue("$id", Key(runId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Snapshots of runs that are not queued or running any more (the process ended before the run released them) are removed;
    /// called when the project opens, after its jobs are recovered.
    /// </summary>
    internal async Task ReleaseOrphanedSnapshotsAsync(IReadOnlySet<Guid> activeRuns, CancellationToken cancellationToken)
    {
        var held = await store.ReadAsync(async (connection, token) =>
        {
            await using var command = Command(connection, null, "SELECT id FROM generation_runs WHERE released_at IS NULL;");
            return await ListAsync(command, reader => ParseId(reader.GetString(0)), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        foreach (var runId in held.Where(id => !activeRuns.Contains(id))) await ReleaseSnapshotAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"&lt;recipe&gt; · dd.MM.yyyy", the recipe name shortened so that the whole name fits 120 characters.</summary>
    internal static string OutputGroupName(string recipeName, DateTime date)
    {
        var suffix = " · " + date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        var name = recipeName.Length + suffix.Length > MaxNameLength ? recipeName[..(MaxNameLength - suffix.Length)].TrimEnd() : recipeName;
        return name + suffix;
    }

    internal static GenerationRunInput? ParseInput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GenerationRunInput>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Deletion checks of other sections ----

    /// <summary>
    /// Presets (archived ones too) that use the catalog object: a binding in classVariantIds, a project variant with a share above
    /// 0 % (a 0 % share does not block) or one of its bindings, a class through its bindings. Count — presets, examples — names.
    /// </summary>
    internal static Task<IReadOnlyList<DeletionBlocker>> CatalogBlockersAsync(ProjectStore store, CatalogTargetKind kind, Guid targetId,
        CancellationToken cancellationToken)
    {
        var filter = kind switch
        {
            CatalogTargetKind.ClassVariant => "EXISTS (SELECT 1 FROM json_each(r.class_variant_ids) b WHERE b.value = $id)",
            CatalogTargetKind.Class => "EXISTS (SELECT 1 FROM json_each(r.class_variant_ids) b JOIN class_variants cv ON cv.id = b.value WHERE cv.class_id = $id)",
            CatalogTargetKind.Variant => """
                EXISTS (SELECT 1 FROM json_each(r.variant_weights) w WHERE json_extract(w.value, '$.variantId') = $id AND json_extract(w.value, '$.percentage') > 0)
                OR EXISTS (SELECT 1 FROM json_each(r.class_variant_ids) b JOIN class_variants cv ON cv.id = b.value WHERE cv.variant_id = $id)
                """,
            _ => null,
        };
        return filter is null ? Task.FromResult<IReadOnlyList<DeletionBlocker>>([]) : RecipeBlockersAsync(store, filter, targetId, cancellationToken);
    }

    /// <summary>
    /// Presets that use the set block deleting it, and deleting backgrounds of it when the deletion takes every active background
    /// (itemIds null — the whole set).
    /// </summary>
    internal static async Task<IReadOnlyList<DeletionBlocker>> BackgroundBlockersAsync(ProjectStore store, Guid setId, IReadOnlyList<Guid>? itemIds,
        CancellationToken cancellationToken)
    {
        if (itemIds is not null)
        {
            var (active, left) = await store.ReadAsync(async (connection, token) =>
            {
                await using var command = Command(connection, null, """
                    SELECT count(*), coalesce(sum(id NOT IN (SELECT value FROM json_each($ids))), 0) FROM background_items WHERE set_id = $set AND removed = 0;
                    """);
                command.Parameters.AddWithValue("$set", Key(setId));
                command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(itemIds.Select(Key)));
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                await reader.ReadAsync(token).ConfigureAwait(false);
                return (reader.GetInt64(0), reader.GetInt64(1));
            }, cancellationToken).ConfigureAwait(false);
            if (active == 0 || left > 0) return [];
        }
        return await RecipeBlockersAsync(store, "EXISTS (SELECT 1 FROM json_each(r.background_set_ids) s WHERE s.value = $id)", setId, cancellationToken)
            .ConfigureAwait(false);
    }

    internal const string PresetReference = "generation.preset_reference";
    private const int BlockerExamples = 5;

    private static Task<IReadOnlyList<DeletionBlocker>> RecipeBlockersAsync(ProjectStore store, string filter, Guid targetId, CancellationToken cancellationToken)
        => store.ReadAsync<IReadOnlyList<DeletionBlocker>>(async (connection, token) =>
        {
            await using var command = Command(connection, null, $"SELECT name FROM generation_recipes r WHERE {filter} ORDER BY created_at, id;");
            command.Parameters.AddWithValue("$id", Key(targetId));
            var names = await ListAsync(command, reader => reader.GetString(0), token).ConfigureAwait(false);
            return names.Count == 0 ? [] : [new DeletionBlocker(PresetReference, names.Count, names.Take(BlockerExamples).ToArray())];
        }, cancellationToken);

    // ---- Validation ----

    /// <summary>Every field of the form; the first wrong one is reported as generation.invalid_recipe with Details["field"].</summary>
    internal static GenerationRecipeInput Validate(GenerationRecipeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim() ?? "";
        if (name.Length is 0 or > MaxNameLength) throw InvalidRecipe("name", "Recipe name must contain between 1 and 120 characters.");
        var description = input.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength) throw InvalidRecipe("description", "Recipe description must not exceed 4000 characters.");
        if (input.Width < CanvasStep || input.Height < CanvasStep || input.Width % CanvasStep != 0 || input.Height % CanvasStep != 0)
            throw InvalidRecipe("canvas", $"Canvas {input.Width}×{input.Height} must be positive multiples of {CanvasStep}.");
        if (!IsIdList(input.BackgroundSetIds)) throw InvalidRecipe("backgroundSetIds", "A recipe uses one or more distinct background sets.");
        if (!IsIdList(input.ClassVariantIds)) throw InvalidRecipe("classVariantIds", "A recipe draws from one or more distinct class variants.");
        var weights = input.VariantWeights;
        if (weights is null || weights.Count == 0 || weights.Any(weight => weight is null || weight.VariantId == Guid.Empty
                || !double.IsFinite(weight.Percentage) || weight.Percentage < 0 || weight.Percentage > 100)
            || weights.Select(weight => weight.VariantId).Distinct().Count() != weights.Count
            || Math.Abs(weights.Sum(weight => weight.Percentage) - 100) > WeightTolerance)
        {
            throw InvalidRecipe("variantWeights", "Variant shares are 0–100 % each and 100 % together.");
        }
        if (input.ObjectsPerImage < 0) throw InvalidRecipe("objectsPerImage", "Objects per image is a whole number from 0.");
        if (!double.IsFinite(input.EmptyProbability) || input.EmptyProbability < 0 || input.EmptyProbability > 1)
            throw InvalidRecipe("emptyProbability", "The empty frame share is between 0 and 1.");
        if (!double.IsFinite(input.Scale) || input.Scale <= 0) throw InvalidRecipe("scale", "The object scale is above 0.");
        return input with
        {
            Name = name,
            Description = description,
            BackgroundSetIds = input.BackgroundSetIds.ToArray(),
            ClassVariantIds = input.ClassVariantIds.ToArray(),
            VariantWeights = weights.Select(weight => new GenerationVariantWeight(weight.VariantId, weight.Percentage)).ToArray(),
        };
    }

    /// <summary>
    /// Saved references must exist (archive is allowed: a run checks availability): the background sets, the project variants of the
    /// shares and the class variants, each of a project variant with a share above 0 %.
    /// </summary>
    private async Task CheckReferencesAsync(GenerationRecipeInput recipe, CancellationToken cancellationToken)
    {
        var sets = await backgrounds.ReadGenerationSourcesAsync(recipe.BackgroundSetIds, cancellationToken).ConfigureAwait(false);
        if (sets.Count != recipe.BackgroundSetIds.Count) throw InvalidRecipe("backgroundSetIds", "A background set of the recipe does not exist.");
        var catalog = await classes.ReadGenerationSourcesAsync(recipe.ClassVariantIds, recipe.VariantWeights.Select(weight => weight.VariantId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (catalog.Variants.Count != recipe.VariantWeights.Count) throw InvalidRecipe("variantWeights", "A project variant of the recipe does not exist.");
        var weighted = recipe.VariantWeights.Where(weight => weight.Percentage > 0).Select(weight => weight.VariantId).ToHashSet();
        if (catalog.Bindings.Count != recipe.ClassVariantIds.Count || catalog.Bindings.Any(binding => !weighted.Contains(binding.VariantId)))
            throw InvalidRecipe("classVariantIds", "A class variant of the recipe does not exist or its project variant has no share.");
    }

    private static bool IsIdList(IReadOnlyList<Guid>? ids) => ids is { Count: > 0 } && !ids.Contains(Guid.Empty) && ids.Distinct().Count() == ids.Count;

    private static bool Same(GenerationRecipeInput left, GenerationRecipeInput right)
        => left.Name == right.Name && left.Description == right.Description && left.Width == right.Width && left.Height == right.Height
            && left.BackgroundSetIds.SequenceEqual(right.BackgroundSetIds) && left.ClassVariantIds.SequenceEqual(right.ClassVariantIds)
            && left.VariantWeights.SequenceEqual(right.VariantWeights) && left.ObjectsPerImage == right.ObjectsPerImage
            && left.EmptyProbability.Equals(right.EmptyProbability) && left.Scale.Equals(right.Scale);

    private static CoreException InvalidRecipe(string field, string message)
        => new("generation.invalid_recipe", message) { Details = new Dictionary<string, string> { ["field"] = field } };

    // ---- Rows ----

    private async Task<GenerationRecipe> ChangeRecipeAsync(Guid recipeId, long expectedRevision,
        Func<GenerationRecipe, (string Set, Action<SqliteCommand> Bind)?> change, CancellationToken cancellationToken)
    {
        var (recipe, saved) = await store.WriteAsync(async (connection, transaction, token) =>
        {
            var current = await FindRecipeAsync(connection, transaction, recipeId, token).ConfigureAwait(false) ?? throw Unavailable(recipeId);
            if (current.Revision != expectedRevision)
                throw RevisionConflict($"Recipe {recipeId} has revision {current.Revision}, expected {expectedRevision}.");
            if (change(current) is not { } update) return (current, false);
            await using var command = Command(connection, transaction,
                $"UPDATE generation_recipes SET {update.Set}, revision = revision + 1, updated_at = $now WHERE id = $id RETURNING {RecipeColumns};");
            command.Parameters.AddWithValue("$id", Key(recipeId));
            command.Parameters.AddWithValue("$now", Time(Now()));
            update.Bind(command);
            return ((await SingleOrDefaultAsync(command, ReadRecipe, token).ConfigureAwait(false))!, true);
        }, cancellationToken).ConfigureAwait(false);
        if (saved) Raise(recipes: [new(recipe.Id, recipe.Revision)]);
        return recipe;
    }

    private static void BindRecipe(SqliteCommand command, GenerationRecipeInput recipe)
    {
        command.Parameters.AddWithValue("$name", recipe.Name);
        command.Parameters.AddWithValue("$description", recipe.Description);
        command.Parameters.AddWithValue("$width", recipe.Width);
        command.Parameters.AddWithValue("$height", recipe.Height);
        command.Parameters.AddWithValue("$sets", JsonSerializer.Serialize(recipe.BackgroundSetIds, Json));
        command.Parameters.AddWithValue("$bindings", JsonSerializer.Serialize(recipe.ClassVariantIds, Json));
        command.Parameters.AddWithValue("$weights", JsonSerializer.Serialize(recipe.VariantWeights, Json));
        command.Parameters.AddWithValue("$objects", recipe.ObjectsPerImage);
        command.Parameters.AddWithValue("$empty", recipe.EmptyProbability);
        command.Parameters.AddWithValue("$scale", recipe.Scale);
    }

    private static async Task<GenerationRecipe?> FindRecipeAsync(SqliteConnection connection, SqliteTransaction? transaction, Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, $"SELECT {RecipeColumns} FROM generation_recipes WHERE id = $id;");
        command.Parameters.AddWithValue("$id", Key(id));
        return await SingleOrDefaultAsync(command, ReadRecipe, cancellationToken).ConfigureAwait(false);
    }

    private static GenerationRecipe ReadRecipe(SqliteDataReader reader) => new(
        ParseId(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetInt32(4),
        JsonSerializer.Deserialize<Guid[]>(reader.GetString(5), Json)!,
        JsonSerializer.Deserialize<Guid[]>(reader.GetString(6), Json)!,
        JsonSerializer.Deserialize<GenerationVariantWeight[]>(reader.GetString(7), Json)!,
        reader.GetInt32(8),
        reader.GetDouble(9),
        reader.GetDouble(10),
        reader.GetInt64(11),
        Time(reader.GetInt64(12)),
        Time(reader.GetInt64(13)),
        reader.IsDBNull(14) ? null : Time(reader.GetInt64(14)));

    /// <summary>Run rows by ID; the manifest (large for big sets) is read only when the run itself needs it.</summary>
    private static async Task<Dictionary<Guid, PreparedRun>> ReadPreparedAsync(SqliteConnection connection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken,
        SqliteTransaction? transaction = null, bool manifest = false)
    {
        var result = new Dictionary<Guid, PreparedRun>();
        if (ids.Count == 0) return result;
        await using var command = Command(connection, transaction, $"""
            SELECT id, output_group_id, {(manifest ? "sources" : "NULL")}, released_at IS NOT NULL FROM generation_runs WHERE id IN (SELECT value FROM json_each($ids));
            """);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Select(Key)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = ParseId(reader.GetString(0));
            result[id] = new PreparedRun(id, reader.IsDBNull(1) ? null : ParseId(reader.GetString(1)),
                reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<GenerationManifest>(reader.GetString(2), Json), reader.GetInt64(3) == 1);
        }
        return result;
    }

    private static GenerationRun? ToRun(StoredJob job, PreparedRun? prepared)
    {
        if (ParseInput(job.Input) is not { } input) return null;
        GenerationCheckpoint? checkpoint = null;
        if (job.Checkpoint is { } json)
        {
            try
            {
                checkpoint = JsonSerializer.Deserialize<GenerationCheckpoint>(json, Json);
            }
            catch (JsonException)
            {
                // A run whose checkpoint cannot be read is blocked; it is listed without published frames.
            }
        }
        return new GenerationRun(job.Info, input.Recipe, input.Seed, input.RequestedCount, checkpoint?.Produced ?? 0, checkpoint?.MissingFrames ?? 0,
            prepared?.OutputGroupId, input.OutputGroupName);
    }

    // ---- Helpers ----

    private void RequireOpen()
    {
        if (!isOpen()) throw new CoreException("generation.project_not_open", $"Project {projectId} is not open; its generation is not available.");
    }

    private void Raise(IReadOnlyList<GenerationEntityChange>? recipes = null, IReadOnlyList<Guid>? runs = null)
        => changed(new GenerationChangedEventArgs(projectId, recipes ?? [], runs ?? []));

    private static CoreException Unavailable(Guid id) => new("generation.unavailable", $"Generation recipe or run {id} was not found or is archived.");

    private static CoreException RevisionConflict(string message) => new("generation.revision_conflict", message);
}
