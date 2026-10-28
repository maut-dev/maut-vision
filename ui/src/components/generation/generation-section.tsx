import { useMemo, useRef, useState, type ReactNode } from "react";
import { generationApi } from "@/api/projects/generation/generation-api";
import { defaultRecipe, randomSeed, type GenerationRecipeField } from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import type { GenerationRunResponseDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { createPalette } from "@/components/images/annotation-palette";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { errorMessage } from "@/lib/format";
import {
    emptyForm,
    formFieldError,
    formForEdit,
    formForRepeat,
    recipeFieldTexts,
    sampleKey,
    toRecipeDto,
    type RecipeFormValues,
} from "@/lib/generation/recipe-form";
import { useBackgroundSets } from "@/lib/use-background-sets";
import { useCatalog } from "@/lib/use-catalog";
import { useGenerationRecipes } from "@/lib/use-generation-recipes";
import { runPageLimit, useGenerationRuns } from "@/lib/use-generation-runs";
import { useGenerationSamples } from "@/lib/use-generation-samples";
import { GenerationDeletionDialog, type GenerationDeletionTarget } from "./generation-deletion-dialog";
import { launchError, LaunchParameters, type LaunchValues } from "./launch-parameters";
import { RecipeCard } from "./recipe-card";
import { RecipeFormPanel, type RecipeFieldError } from "./recipe-form-panel";
import { RunCard } from "./run-card";

type Tab = "recipes" | "runs";

interface Notice {
    tone: "info" | "error";
    text: string;
    /** «Показать запуски» после постановки запуска. */
    showRuns?: boolean;
}

const fields: readonly GenerationRecipeField[] = ["name", "description", "canvas", "backgroundSetIds", "classVariantIds", "variantWeights", "objectsPerImage",
    "emptyProbability", "scale"];

/**
 * Раздел «Генерация»: вкладки «Пресеты (N)» и «Запуски (N)». Пресеты — слева форма (новый, изменение, повтор запуска) с
 * «Пример кадра», справа «Параметры запуска» и карточки пресетов. Запуски — карточки с прогрессом по событиям. Без опроса:
 * generation.changed, job.changed, catalog.changed, backgrounds.changed. Форма и параметры хранятся здесь, поэтому
 * переживают переключение вкладок.
 */
export function GenerationSection({ project, openImages }: { project: ProjectResponseDto; openImages: (groupId: string) => void }) {
    const projectId = project.id;
    const catalog = useCatalog(projectId);
    const sets = useBackgroundSets(projectId);
    const recipes = useGenerationRecipes(projectId);
    const runs = useGenerationRuns(projectId);
    const samples = useGenerationSamples(projectId);
    const palette = useMemo(() => createPalette(catalog.workspace), [catalog.workspace]);
    const [tab, setTab] = useState<Tab>("recipes");
    const [form, setForm] = useState<RecipeFormValues>(emptyForm);
    const [fieldError, setFieldError] = useState<RecipeFieldError>();
    const [launch, setLaunch] = useState<LaunchValues>(() => ({ count: defaultRecipe.count, seed: randomSeed() }));
    const [advancedOpen, setAdvancedOpen] = useState(false);
    const [showArchived, setShowArchived] = useState(false);
    const [deletion, setDeletion] = useState<GenerationDeletionTarget>();
    const [notice, setNotice] = useState<Notice>();
    const [busy, setBusy] = useState(false);
    const nameRef = useRef<HTMLInputElement>(null);

    const workspace = catalog.workspace;
    const input = project.inputSize;
    const dto = toRecipeDto(form, workspace, input);
    const invalidField = formFieldError(dto);
    const runInvalid = launchError(launch);
    const samplesStale = samples.samples.length > 0 && samples.key !== sampleKey(dto, launch.seed);
    const editing = form.mode.kind === "edit" ? form.mode.recipe.id : undefined;
    const visibleRecipes = recipes.recipes.filter(recipe => showArchived || !recipe.archivedAt);

    function updateForm(update: (current: RecipeFormValues) => RecipeFormValues) {
        setForm(update);
        setFieldError(undefined);
    }

    function focusLater(selector: string | (() => HTMLElement | null | undefined)) {
        requestAnimationFrame(() => (typeof selector === "string" ? document.querySelector<HTMLElement>(selector) : selector())?.focus());
    }

    /** Текст ошибки host; конфликт ревизии перечитывает пресеты, неверное поле подсвечивается в форме. */
    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError) {
            if (cause.code === "generation.revision_conflict") {
                void recipes.refresh();
                return `${message} Список обновлён.`;
            }
            const field = cause.details?.field as GenerationRecipeField | undefined;
            if (cause.code === "generation.invalid_recipe" && field && fields.includes(field)) {
                setFieldError({ field, message: `${recipeFieldTexts[field]} ${message}` });
            }
        }
        return message;
    }

    async function mutate(action: () => Promise<void>): Promise<boolean> {
        setBusy(true);
        try {
            await action();
            return true;
        } catch (cause) {
            setNotice({ tone: "error", text: describe(cause) });
            return false;
        } finally {
            setBusy(false);
        }
    }

    function finishEdit(recipeId: string | undefined) {
        setForm(emptyForm());
        setFieldError(undefined);
        if (recipeId) focusLater(`[data-generation-recipe-id="${recipeId}"] [data-testid="recipe-edit"]`);
    }

    function save() {
        const mode = form.mode;
        setNotice(undefined);
        setFieldError(undefined);
        void mutate(async () => {
            const saved = mode.kind === "edit"
                ? await generationApi.updateRecipe({ projectId, recipeId: mode.recipe.id, recipe: dto, revision: mode.recipe.revision })
                : await generationApi.createRecipe({ projectId, recipe: dto });
            recipes.apply(saved);
            if (saved.archivedAt) setShowArchived(true);
            setNotice({ tone: "info", text: mode.kind === "edit" ? `Пресет «${saved.name}» сохранён, версия ${saved.revision}.` : `Пресет «${saved.name}» сохранён.` });
            if (mode.kind === "create") setForm(current => ({ ...current, name: "" }));
            else finishEdit(mode.kind === "edit" ? saved.id : undefined);
        });
    }

    function cancelForm() {
        finishEdit(editing);
    }

    function requestSamples() {
        if (invalidField) return;
        if (runInvalid) {
            setNotice({ tone: "error", text: runInvalid });
            setAdvancedOpen(true);
            return;
        }
        void samples.request(dto, launch.seed);
    }

    function edit(recipe: GenerationRecipeResponseDto) {
        setNotice(undefined);
        setFieldError(undefined);
        setForm(formForEdit(recipe));
        focusLater(() => nameRef.current);
    }

    function toggleArchive(recipe: GenerationRecipeResponseDto) {
        setNotice(undefined);
        void mutate(async () => {
            const saved = await generationApi.setRecipeArchived({ projectId, recipeId: recipe.id, archived: !recipe.archivedAt, revision: recipe.revision });
            recipes.apply(saved);
            if (editing === recipe.id) finishEdit(undefined);
            // The card stays visible after a restore; after archiving it hides unless archived recipes are shown.
            focusLater(`[data-generation-recipe-id="${recipe.id}"] [data-testid="recipe-archive"]`);
        });
    }

    function start(recipe: GenerationRecipeResponseDto) {
        if (runInvalid) return;
        setNotice(undefined);
        void mutate(async () => {
            const run = await generationApi.start({
                projectId, recipeId: recipe.id, recipeRevision: recipe.revision, seed: launch.seed, count: launch.count, operationId: crypto.randomUUID(),
            });
            runs.apply(run);
            setNotice({ tone: "info", text: `Запуск «${recipe.name}» поставлен в очередь: ${run.requestedCount.toLocaleString("ru-RU")} изображений.`, showRuns: true });
        });
    }

    function repeat(run: GenerationRunResponseDto) {
        setNotice(undefined);
        void mutate(async () => {
            const draft = await generationApi.getRepeatDraft({ projectId, runId: run.job.id });
            setFieldError(undefined);
            setForm(formForRepeat(draft));
            setLaunch({ count: draft.requestedCount, seed: draft.seed });
            setTab("recipes");
            focusLater(() => nameRef.current);
        });
    }

    function cancelRun(run: GenerationRunResponseDto) {
        void runs.cancel(run).catch(cause => setNotice({ tone: "error", text: errorMessage(cause) }));
    }

    function deleted(target: GenerationDeletionTarget) {
        setDeletion(undefined);
        if (target.kind === "recipe") {
            recipes.remove(target.id);
            if (editing === target.id) finishEdit(undefined);
        } else runs.remove(target.id);
    }

    function selectTab(next: Tab) {
        setTab(next);
        setNotice(current => current?.showRuns && next === "runs" ? undefined : current);
    }

    const loadError = catalog.error ?? sets.error ?? recipes.error;
    return (
        <div className="flex min-h-0 flex-1 flex-col gap-3 overflow-hidden" data-testid="generation-section">
            {notice && (
                <Alert variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="generation-notice">
                    <AlertDescription>
                        <p>{notice.text}</p>
                        <div className="mt-1 flex gap-2">
                            {notice.showRuns && <Button variant="outline" size="sm" data-testid="notice-show-runs" onClick={() => selectTab("runs")}>Показать запуски</Button>}
                            <Button variant="ghost" size="sm" onClick={() => setNotice(undefined)}>Скрыть</Button>
                        </div>
                    </AlertDescription>
                </Alert>
            )}
            {loadError && (
                <Alert variant="destructive" className="shrink-0">
                    <AlertDescription>
                        <p>{loadError}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => { void catalog.refresh(); void sets.refresh(); void recipes.refresh(); }}>Повторить</Button>
                    </AlertDescription>
                </Alert>
            )}
            <div className="flex shrink-0 flex-wrap gap-2" role="group" aria-label="Разделы генерации">
                <Button variant={tab === "recipes" ? "secondary" : "ghost"} aria-pressed={tab === "recipes"} data-testid="tab-recipes" onClick={() => selectTab("recipes")}>
                    Пресеты ({recipes.recipes.length})
                </Button>
                <Button variant={tab === "runs" ? "secondary" : "ghost"} aria-pressed={tab === "runs"} data-testid="tab-runs" onClick={() => selectTab("runs")}>
                    Запуски ({runs.total})
                </Button>
            </div>
            <div className={tab === "recipes" ? "grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_minmax(0,1fr)] gap-5" : "hidden"}>
                <section className="min-h-0 overflow-y-auto overscroll-contain pr-2" aria-label="Настройки пресета" data-testid="recipe-form-panel">
                    <RecipeFormPanel
                        values={form}
                        setValues={updateForm}
                        workspace={workspace}
                        sets={sets.sets}
                        input={input}
                        palette={palette}
                        samples={samples}
                        samplesStale={samplesStale}
                        fieldError={fieldError}
                        invalidField={invalidField}
                        busy={busy}
                        nameRef={nameRef}
                        save={save}
                        cancel={cancelForm}
                        requestSamples={requestSamples}
                    />
                </section>
                <section className="min-h-0 space-y-4 overflow-y-auto overscroll-contain pr-2" aria-label="Сохранённые пресеты" data-testid="recipe-list">
                    <LaunchParameters values={launch} setValues={setLaunch} advancedOpen={advancedOpen} setAdvancedOpen={setAdvancedOpen} />
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox checked={showArchived} data-testid="show-archived-recipes" onCheckedChange={value => setShowArchived(value === true)} />
                        Показать архивные пресеты
                    </label>
                    {visibleRecipes.map(recipe => (
                        <RecipeCard
                            key={recipe.id}
                            recipe={recipe}
                            workspace={workspace}
                            sets={sets.sets}
                            count={launch.count}
                            canStart={!runInvalid}
                            editing={editing === recipe.id}
                            busy={busy}
                            start={() => start(recipe)}
                            edit={() => edit(recipe)}
                            toggleArchive={() => toggleArchive(recipe)}
                            remove={() => setDeletion({ kind: "recipe", id: recipe.id, name: recipe.name, revision: recipe.revision })}
                        />
                    ))}
                    {recipes.loading && !recipes.recipes.length && <p role="status" className="text-sm text-muted-foreground">Загрузка пресетов…</p>}
                    {!recipes.loading && !visibleRecipes.length && (
                        <Empty testId="recipes-empty">Сохраните пресет для следующих генераций. Архивные пресеты доступны через переключатель выше.</Empty>
                    )}
                </section>
            </div>
            {/* Both tabs stay mounted: the form survives switching, run cards follow job.changed while hidden. */}
            <section className={tab === "runs" ? "min-h-0 flex-1 space-y-4 overflow-y-auto overscroll-contain pr-2" : "hidden"} aria-label="История генерации" data-testid="run-list">
                {runs.error && (
                    <Alert variant="destructive">
                        <AlertDescription>
                            <p>{runs.error}</p>
                            <Button variant="ghost" size="sm" className="mt-1" onClick={() => void runs.refresh()}>Повторить</Button>
                        </AlertDescription>
                    </Alert>
                )}
                {runs.runs.map(run => (
                    <RunCard
                        key={run.job.id}
                        run={run}
                        workspace={workspace}
                        sets={sets.sets}
                        busy={busy}
                        openImages={openImages}
                        cancel={() => cancelRun(run)}
                        repeat={() => repeat(run)}
                        remove={() => setDeletion({ kind: "run", id: run.job.id, name: run.recipe.name })}
                    />
                ))}
                {runs.total > runs.runs.length && (
                    <p className="text-xs text-muted-foreground">Показаны последние {Math.max(runPageLimit, runs.runs.length)} запусков из {runs.total.toLocaleString("ru-RU")}.</p>
                )}
                {runs.loading && !runs.runs.length && <p role="status" className="text-sm text-muted-foreground">Загрузка запусков…</p>}
                {!runs.loading && !runs.error && !runs.runs.length && <Empty testId="runs-empty">Выберите пресет и создайте изображения. Здесь появится история запусков.</Empty>}
            </section>
            {deletion && <GenerationDeletionDialog projectId={projectId} target={deletion} close={() => setDeletion(undefined)} deleted={deleted} />}
        </div>
    );
}

function Empty({ testId, children }: { testId: string; children: ReactNode }) {
    return (
        <div className="flex min-h-40 flex-col items-center justify-center rounded-md border border-dashed p-6 text-center text-sm text-muted-foreground" data-testid={testId}>
            {children}
        </div>
    );
}
