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

/** Примеры декодируют изображения классов и фоны: ответ может прийти позже обычных 10 с. */
const previewTimeoutMs = 30_000;

/**
 * Генерация открытого проекта: пресеты и запуски.
 *
 * Пресеты: listRecipes (все, включая архивные; «Показать архивные пресеты» фильтрует UI), createRecipe / updateRecipe с revision,
 * setRecipeArchived, deleteRecipe с revision. Форма собирает GenerationRecipeFormDto: classVariantIds — recipeClassVariantIds(workspace,
 * weights), доли — по доступным вариантам проекта («Поровну» — distributeEvenly), до отправки — recipeFieldError. previewSamples —
 * «Пример кадра»: до 4 кадров формы с seed запуска (временные ресурсы с рамками).
 *
 * Запуски: start возвращает запуск сразу (job.status queued); прогресс, состояние и итог — событие job.changed (kind generation.run,
 * parseGenerationRunResult), применять снимок с большим sequence; «Отменить» — jobsApi.cancel(projectId, run.job.id); listRuns /
 * getRun при открытии и после generation.changed (runs — подготовлен или удалён); deleteRun — только завершённый, изображения
 * остаются; getRepeatDraft — «Настроить повтор». Группа результата появляется в images.changed, её изображения уже размечены
 * (reviewed).
 *
 * Изменения: generation.changed (subscribeGenerationChanged) — recipes с ревизиями (null — удалён) и runs.
 *
 * Ошибки: generation.project_not_open, generation.invalid_recipe (error.details.field — поле формы), generation.revision_conflict,
 * generation.recipe_archived, generation.unavailable, generation.deletion_blocked (удаление пресета с незавершённым запуском),
 * generation.run_active, generation.deleted_operation, generation.count_invalid, generation.seed_invalid; проверки запуска и
 * примеров: generation.canvas_mismatch, generation.background_size_mismatch, generation.backgrounds_unavailable,
 * generation.variant_unavailable, generation.variant_without_asset, generation.unsupported_asset, generation.backgrounds_empty;
 * ошибки задачи (job.error): те же и generation.preparation_failed, generation.execution_failed, generation.disk_full,
 * generation.out_of_memory, generation.interrupted; jobs.project_not_open. Тексты приходят готовыми в error.message.
 */
export const generationApi = {
    get available(): boolean { return nativeBridge.available; },
    listRecipes(request: ListGenerationRecipesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.listRecipes", request, isGenerationRecipeList, signal);
    },
    getRecipe(request: GetGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** «Сохранить рецепт»: новый пресет с revision 1. */
    createRecipe(request: CreateGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.createRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** «Изменить пресет»: новая ревизия; архивный пресет не меняется. */
    updateRecipe(request: UpdateGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.updateRecipe", request, isGenerationRecipeResponseDto, signal);
    },
    /** «В архив» / «Восстановить». */
    setRecipeArchived(request: SetGenerationRecipeArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.setRecipeArchived", request, isGenerationRecipeResponseDto, signal);
    },
    /** Удаляет пресет; запуски хранят его копию и остаются. */
    deleteRecipe(request: DeleteGenerationRecipeRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.deleteRecipe", request, isGenerationRecipeDeletionResponseDto, signal);
    },
    /** «Пример кадра»: те же пиксели и рамки, что даст запуск с этим seed для первых кадров; ничего не сохраняет. */
    previewSamples(request: PreviewGenerationSamplesRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.previewSamples", request, isGenerationSamplesResponseDto, signal, previewTimeoutMs);
    },
    listRuns(request: ListGenerationRunsRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.listRuns", request, isGenerationRunPageResponseDto, signal);
    },
    getRun(request: GetGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRun", request, isGenerationRunResponseDto, signal);
    },
    /** «Сгенерировать N изображений»: проверяет пресет и источники и ставит запуск; повтор с тем же operationId вернёт тот же запуск. */
    start(request: StartGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.start", request, isGenerationRunResponseDto, signal, previewTimeoutMs);
    },
    deleteRun(request: DeleteGenerationRunRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.deleteRun", request, isGenerationRunDeletionResponseDto, signal);
    },
    /** «Настроить повтор»: настройки запуска для формы, его seed и количество, уведомление. */
    getRepeatDraft(request: GetGenerationRepeatDraftRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("generation.getRepeatDraft", request, isGenerationRepeatDraftResponseDto, signal);
    },
};
