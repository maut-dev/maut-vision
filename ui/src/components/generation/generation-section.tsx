import { useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { generationApi } from "@/api/projects/generation/generation-api";
import { defaultRecipe, randomSeed, type GenerationRecipeField } from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import type { GenerationRunResponseDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { createPalette } from "@/components/images/annotation-palette";
import { ActiveJobs } from "@/components/jobs/job-history";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { SectionLayout } from "@/components/layout/section-layout";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { Toolbar } from "@/components/layout/toolbar";
import { Checkbox } from "@/components/ui/checkbox";
import { errorMessage, formatNumber } from "@/lib/format";
import { isRunActive } from "@/lib/generation/run-state";
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
import { useFocusAfterRender } from "@/lib/use-focus-after-render";
import { useGenerationRecipes } from "@/lib/use-generation-recipes";
import { runPageLimit, useGenerationRuns } from "@/lib/use-generation-runs";
import { useGenerationSamples } from "@/lib/use-generation-samples";
import { GenerationDeletionDialog, type GenerationDeletionTarget } from "./generation-deletion-dialog";
import { launchError, LaunchParameters, type LaunchValues } from "./launch-parameters";
import { RecipeCard } from "./recipe-card";
import { RecipeFormPanel, type RecipeFieldError } from "./recipe-form-panel";
import { RunCard } from "./run-card";

type Tab = "recipes" | "runs";

interface NoticeState {
    tone: "info" | "error";
    text: string;
    /** "Show runs" after a run was queued. */
    showRuns?: boolean;
}

const fields: readonly GenerationRecipeField[] = ["name", "description", "canvas", "backgroundSetIds", "classVariantIds", "variantWeights", "objectsPerImage",
    "emptyProbability", "scale"];

/**
 * The section "Generation": tabs "Presets N" and "Runs N" (a live dot while a run is queued or running). Presets — the form (new, change,
 * repeat of a run) with "Sample frame", and beside it the running runs, "Run parameters" and preset cards. Runs — cards with progress by
 * events. No polling: generation.changed, job.changed, catalog.changed, backgrounds.changed. The form and the parameters live here, so
 * they survive tab switches.
 */
export function GenerationSection({ project, openImages }: { project: ProjectResponseDto; openImages: (groupId: string) => void }) {
    const { t } = useTranslation("generation");
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
    const [notice, setNotice] = useState<NoticeState>();
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

    const focusAfterRender = useFocusAfterRender(busy);

    /** Focus after the render that enables the target: the card buttons are disabled while busy and while the recipe is edited. */
    function focusLater(selector: string | (() => HTMLElement | null | undefined)) {
        focusAfterRender(typeof selector === "string" ? () => document.querySelector<HTMLElement>(selector) : selector);
    }

    /** The text of a host error; a revision conflict reads the presets again, a wrong field is highlighted in the form. */
    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError) {
            if (cause.code === "generation.revision_conflict") {
                void recipes.refresh();
                return t("section.listRefreshed", { message });
            }
            const field = cause.details?.field as GenerationRecipeField | undefined;
            if (cause.code === "generation.invalid_recipe" && field && fields.includes(field)) {
                setFieldError({ field, message: t("section.fieldError", { field: recipeFieldTexts[field], message }) });
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
            setNotice({ tone: "info", text: mode.kind === "edit"
                ? t("section.savedRevision", { name: saved.name, revision: saved.revision })
                : t("section.saved", { name: saved.name }) });
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
            setNotice({ tone: "info", text: t("section.queued", { name: recipe.name, count: run.requestedCount, value: formatNumber(run.requestedCount) }), showRuns: true });
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
    const runJobs = runs.runs.map(run => run.job);
    const running = runJobs.some(isRunActive);
    return (
        <SectionLayout data-testid="generation-section">
            {notice && (
                <Notice variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="generation-notice"
                    action={notice.showRuns ? { label: t("section.showRuns"), onClick: () => selectTab("runs"), testId: "notice-show-runs" } : undefined}
                    onDismiss={() => setNotice(undefined)}>
                    {notice.text}
                </Notice>
            )}
            {loadError && (
                <Notice variant="destructive" className="shrink-0"
                    action={{ label: t("section.retry"), onClick: () => { void catalog.refresh(); void sets.refresh(); void recipes.refresh(); } }}>
                    {loadError}
                </Notice>
            )}
            <Tabs value={tab} onValueChange={value => selectTab(value as Tab)} className="flex-1">
                <TabsList aria-label={t("section.tabs")}>
                    <TabsTrigger value="recipes" count={recipes.recipes.length} data-testid="tab-recipes">{t("section.presetsTab")}</TabsTrigger>
                    <TabsTrigger value="runs" count={runs.total} live={running} data-testid="tab-runs">{t("section.runsTab")}</TabsTrigger>
                </TabsList>
                {/*
                  Both tabs stay mounted: the form survives switching, run cards follow job.changed while hidden. At lg the form and the
                  presets are two columns with a scroll each; below lg they stack in the one scroll of the tab.
                */}
                <TabsContent value="recipes" forceMount hidden={tab !== "recipes"}
                    className="flex flex-col gap-4 overflow-y-auto overscroll-contain lg:grid lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,1fr)] lg:overflow-visible">
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
                    <section className="flex min-w-0 flex-col gap-3 lg:min-h-0" aria-label={t("section.savedPresets")}>
                        <ActiveJobs jobs={runJobs} label={t("section.activeRuns")} progressLabel={t("section.activeProgress")} testId="generation-active-runs"
                            action={{ label: t("section.showRuns"), onClick: () => selectTab("runs") }} />
                        <LaunchParameters values={launch} setValues={setLaunch} advancedOpen={advancedOpen} setAdvancedOpen={setAdvancedOpen} />
                        <Toolbar className="shrink-0">
                            <label className="flex items-center gap-2 text-[13px] text-text-2">
                                <Checkbox checked={showArchived} data-testid="show-archived-recipes" onCheckedChange={value => setShowArchived(value === true)} />
                                {t("section.showArchived")}
                            </label>
                        </Toolbar>
                        <div role="region" aria-label={t("section.presetList")} data-testid="recipe-list"
                            className="space-y-3 lg:min-h-0 lg:flex-1 lg:overflow-y-auto lg:overscroll-contain">
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
                            {recipes.loading && !recipes.recipes.length && <p role="status" className="text-sm text-text-2">{t("section.presetsLoading")}</p>}
                            {!recipes.loading && !visibleRecipes.length && <Empty testId="recipes-empty">{t("section.presetsEmpty")}</Empty>}
                        </div>
                    </section>
                </TabsContent>
                <TabsContent value="runs" forceMount hidden={tab !== "runs"} className="flex flex-col">
                    <Pane role="region" className="flex-1 space-y-3" aria-label={t("section.history")} data-testid="run-list">
                        {runs.error && <Notice variant="destructive" action={{ label: t("section.retry"), onClick: () => void runs.refresh() }}>{runs.error}</Notice>}
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
                            <p className="text-xs text-text-2">{t("section.lastRuns", { shown: Math.max(runPageLimit, runs.runs.length), total: formatNumber(runs.total) })}</p>
                        )}
                        {runs.loading && !runs.runs.length && <p role="status" className="text-sm text-text-2">{t("section.runsLoading")}</p>}
                        {!runs.loading && !runs.error && !runs.runs.length && <Empty testId="runs-empty">{t("section.runsEmpty")}</Empty>}
                    </Pane>
                </TabsContent>
            </Tabs>
            {deletion && <GenerationDeletionDialog projectId={projectId} target={deletion} close={() => setDeletion(undefined)} deleted={deleted} />}
        </SectionLayout>
    );
}

function Empty({ testId, children }: { testId: string; children: string }) {
    return <EmptyState title={children} className="rounded-lg border border-dashed border-line" data-testid={testId} />;
}
