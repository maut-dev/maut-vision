import { nativeBridge } from "../../transport/native-bridge";
import type {
    CreateGenerationRecipeRequestDto, DeleteGenerationRecipeRequestDto, GetGenerationRecipeRequestDto, ListGenerationRecipesRequestDto,
    PreviewGenerationSamplesRequestDto, SetGenerationRecipeArchivedRequestDto, UpdateGenerationRecipeRequestDto,
} from "./request-dto/generation-recipe-request-dto";
import type {
    DeleteGenerationRunRequestDto, GetGenerationRepeatDraftRequestDto, GetGenerationRunRequestDto, ListGenerationRunsRequestDto,
    StartGenerationRunRequestDto,
} from "./request-dto/generation-run-request-dto";
import { isGenerationRecipeDeletionResponseDto, isGenerationRecipeList, isGenerationRecipeResponseDto } from "./response-dto/generation-recipe-response-dto";
import { isGenerationRunDeletionResponseDto, isGenerationRunPageResponseDto, isGenerationRunResponseDto } from "./response-dto/generation-run-response-dto";
import { isGenerationRepeatDraftResponseDto, isGenerationSamplesResponseDto } from "./response-dto/generation-sample-response-dto";

/** Samples decode class images and backgrounds: the answer may come later than the usual 10 s. */
const previewTimeoutMs = 30_000;

/**
 * Generation of the open project: presets and runs.
 *
 * Presets: listRecipes (all, archived ones too; "Show archived presets" filters in the UI), createRecipe / updateRecipe with revision,
 * setRecipeArchived, deleteRecipe with revision. The form builds GenerationRecipeFormDto: classVariantIds — recipeClassVariantIds(workspace,
 * weights), shares — by the available project variants ("Evenly" — distributeEvenly), before sending — recipeFieldError. previewSamples —
 * "Sample frame": up to 4 frames of the form with the run seed (temporary resources with boxes).
 *
 * Runs: start returns the run at once (job.status queued); progress, state and result — the event job.changed (kind generation.run,
 * parseGenerationRunResult), apply the snapshot with a larger sequence; "Cancel" — jobsApi.cancel(projectId, run.job.id); listRuns /
 * getRun on opening and after generation.changed (runs — prepared or deleted); deleteRun — only a finished one, the images
 * stay; getRepeatDraft — "Set up a repeat". The result group appears in images.changed, its images are already annotated
 * (reviewed).
 *
 * Changes: generation.changed (subscribeGenerationChanged) — recipes with revisions (null — deleted) and runs.
 *
 * Errors: generation.project_not_open, generation.invalid_recipe (error.details.field — the form field), generation.revision_conflict,
 * generation.recipe_archived, generation.unavailable, generation.deletion_blocked (deleting a preset with an unfinished run),
 * generation.run_active, generation.deleted_operation, generation.count_invalid, generation.seed_invalid; checks of the run and
 * samples: generation.canvas_mismatch, generation.background_size_mismatch, generation.backgrounds_unavailable,
 * generation.variant_unavailable, generation.variant_without_asset, generation.unsupported_asset, generation.backgrounds_empty;
 * job errors (job.error): the same and generation.preparation_failed, generation.execution_failed, generation.disk_full,
 * generation.out_of_memory, generation.interrupted; jobs.project_not_open. Errors come as codes; texts are in the errors catalog.
 */
export const generationApi = {
    get available(): boolean { return nativeBridge.available; },
    listRecipes(request: ListGenerationRecipesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.listRecipes", request, isGenerationRecipeList, signal);
    },
    getRecipe(request: GetGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** "Save recipe": a new preset with revision 1. */
    createRecipe(request: CreateGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.createRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** "Edit preset": a new revision; an archived preset does not change. */
    updateRecipe(request: UpdateGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.updateRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** "Archive" / "Restore". */
    setRecipeArchived(request: SetGenerationRecipeArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.setRecipeArchived", request, isGenerationRecipeResponseDto, signal);
    },
    /** Deletes a preset; runs keep its copy and stay. */
    deleteRecipe(request: DeleteGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.deleteRecipe", request, isGenerationRecipeDeletionResponseDto, signal);
    },
    /** "Sample frame": the same pixels and boxes a run with this seed gives for its first frames; saves nothing. */
    previewSamples(request: PreviewGenerationSamplesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.previewSamples", request, isGenerationSamplesResponseDto, signal, previewTimeoutMs);
    },
    listRuns(request: ListGenerationRunsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.listRuns", request, isGenerationRunPageResponseDto, signal);
    },
    getRun(request: GetGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRun", request, isGenerationRunResponseDto, signal);
    },
    /** "Generate N images": checks the preset and the sources and queues the run; a repeat with the same operationId returns the same run. */
    start(request: StartGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.start", request, isGenerationRunResponseDto, signal, previewTimeoutMs);
    },
    deleteRun(request: DeleteGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.deleteRun", request, isGenerationRunDeletionResponseDto, signal);
    },
    /** "Set up a repeat": the run settings for the form, its seed and count, a notice. */
    getRepeatDraft(request: GetGenerationRepeatDraftRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRepeatDraft", request, isGenerationRepeatDraftResponseDto, signal);
    },
};
