import { useId, type FormEvent, type ReactNode, type RefObject } from "react";
import { Trans, useTranslation } from "react-i18next";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import { generationTexts, maxRecipeNameLength, type GenerationRecipeField } from "@/api/projects/generation/generation-rules";
import type { AnnotationPalette } from "@/components/images/annotation-palette";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Slider } from "@/components/ui/slider";
import { setSummary } from "@/lib/backgrounds/background-text";
import {
    backgroundSetOption,
    canvasOf,
    clampPercent,
    classesLabel,
    evenWeights,
    formTotal,
    leaveSnapshot,
    objectsHint,
    variantShares,
    type RecipeFormValues,
} from "@/lib/generation/recipe-form";
import type { GenerationSamplesState } from "@/lib/use-generation-samples";
import { GenerationSamples } from "./generation-samples";
import { RecipeDetails } from "./recipe-details";
import { formatNumber } from "@/lib/format";

/** A field error from the host (generation.invalid_recipe → details.field) or from the check before sending. */
export interface RecipeFieldError {
    field: GenerationRecipeField;
    message: string;
}

/**
 * The form "New preset" / "Edit preset" (and a repeat of a run): the name, background sets (sets of another size are shown but
 * unavailable), variant shares (a field and a slider, "Evenly", "Total: X% of 100%"), objects, scale, empty frames, "Sample frame" and
 * "Save recipe". The owner keeps the values so the form survives tab switches.
 */
export function RecipeFormPanel({ values, setValues, workspace, sets, input, palette, samples, samplesStale, fieldError, invalidField, busy, nameRef,
    save, cancel, requestSamples }: {
    values: RecipeFormValues;
    setValues: (update: (current: RecipeFormValues) => RecipeFormValues) => void;
    workspace: CatalogWorkspaceResponseDto | undefined;
    sets: readonly BackgroundSetResponseDto[];
    input: { width: number; height: number };
    palette: AnnotationPalette;
    samples: GenerationSamplesState;
    samplesStale: boolean;
    fieldError?: RecipeFieldError;
    /** The first wrong field by the host rules: saving and the sample are unavailable. */
    invalidField: GenerationRecipeField | null;
    busy: boolean;
    nameRef: RefObject<HTMLInputElement | null>;
    save: () => void;
    cancel: () => void;
    requestSamples: () => void;
}) {
    const { t } = useTranslation("generation");
    const id = useId();
    const mode = values.mode;
    const repeat = mode.kind === "repeat" ? mode : undefined;
    const canvas = canvasOf(values, input);
    const shares = variantShares(workspace, values.weights);
    const available = shares.filter(share => share.eligible);
    const total = formTotal(values, workspace);
    const totalValid = Math.abs(total - 100) <= 1e-6;
    const hint = objectsHint(values);
    const visibleSets = sets.filter(set => !set.archivedAt || values.setIds.includes(set.id));
    const missingSets = values.setIds.filter(setId => !sets.some(set => set.id === setId));
    const update = (patch: Partial<RecipeFormValues>) => setValues(current => ({ ...current, ...patch }));
    const toggleSet = (setId: string) => setValues(current => ({
        ...current,
        setIds: current.setIds.includes(setId) ? current.setIds.filter(item => item !== setId) : [...current.setIds, setId],
    }));
    const setWeight = (variantId: string, percentage: number) => setValues(current => ({ ...current, weights: { ...current.weights, [variantId]: percentage } }));
    const errorOf = (...fields: GenerationRecipeField[]) => fieldError && fields.includes(fieldError.field) ? fieldError.message : undefined;

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && !invalidField) save();
    }

    return (
        <form className="space-y-5 rounded-lg border bg-card p-5" onSubmit={submit} aria-labelledby={`${id}-title`} data-testid="recipe-form" data-mode={mode.kind} noValidate>
            <div className="flex flex-wrap items-baseline justify-between gap-2">
                <h2 id={`${id}-title`} className="font-medium" data-testid="recipe-form-title">{mode.kind === "edit" ? t("form.editTitle") : t("form.newTitle")}</h2>
                {mode.kind === "edit" && <span className="text-xs text-muted-foreground" data-testid="recipe-form-target">{t("form.target", { name: mode.recipe.name, revision: mode.recipe.revision })}</span>}
            </div>
            {repeat && (
                <div role="status" className="space-y-2 text-sm" data-testid="repeat-notice">
                    <p>{repeat.notice}</p>
                    <p className="text-xs text-muted-foreground">{generationTexts.repeatHint}</p>
                    <Button type="button" variant="outline" size="sm" className="h-auto min-h-9 max-w-full min-w-0 whitespace-normal" data-testid="repeat-current-sources"
                        onClick={() => setValues(leaveSnapshot)}>
                        {t("form.currentSources")}
                    </Button>
                </div>
            )}
            <p className="text-sm text-muted-foreground">{t("form.intro")}</p>
            <FormField label={t("form.name")} htmlFor={`${id}-name`} error={errorOf("name", "description")}>
                <Input id={`${id}-name`} ref={nameRef} required maxLength={maxRecipeNameLength} data-testid="recipe-name" aria-invalid={!!errorOf("name") || undefined}
                    value={values.name} onChange={event => update({ name: event.target.value })} />
            </FormField>
            <fieldset className="space-y-2" aria-describedby={errorOf("backgroundSetIds", "canvas") ? `${id}-sets-error` : undefined} data-testid="recipe-sets-field">
                <legend className="mb-2 text-sm font-medium">{t("form.sets")}</legend>
                <div className="max-h-52 space-y-2 overflow-y-auto pr-1">
                    {visibleSets.map(set => {
                        const option = backgroundSetOption(set, canvas);
                        const checked = values.setIds.includes(set.id);
                        const disabled = !option.fits && !checked;
                        return (
                            <div key={set.id} className="space-y-0.5" data-generation-set-id={set.id} data-fits={option.fits || undefined}>
                                <label className={`flex items-center gap-2 text-sm ${disabled ? "text-muted-foreground" : ""}`}>
                                    <Checkbox checked={checked} disabled={disabled} aria-describedby={option.reason ? `${id}-set-${set.id}` : undefined}
                                        onCheckedChange={() => toggleSet(set.id)} />
                                    <span className="min-w-0 break-words" data-testid="set-name">{set.name}</span>
                                    <span className="text-xs text-muted-foreground tabular-nums">
                                        {setSummary(set)}
                                    </span>
                                </label>
                                {option.reason && <p id={`${id}-set-${set.id}`} className="pl-6 text-xs text-amber-700 dark:text-amber-400" data-testid="set-incompatible">{option.reason}</p>}
                            </div>
                        );
                    })}
                    {missingSets.map(setId => (
                        <label key={setId} className="flex items-center gap-2 text-sm text-muted-foreground">
                            <Checkbox checked onCheckedChange={() => toggleSet(setId)} />{t("form.setMissing")}
                        </label>
                    ))}
                    {!sets.length && <p className="text-xs text-muted-foreground">{t("form.noSets")}</p>}
                </div>
                {errorOf("backgroundSetIds", "canvas") && <p id={`${id}-sets-error`} className="text-xs text-destructive" role="alert">{errorOf("backgroundSetIds", "canvas")}</p>}
            </fieldset>
            <section className="space-y-3" aria-labelledby={`${id}-shares`}>
                {repeat ? (
                    <>
                        <h3 id={`${id}-shares`} className="text-sm font-medium">{t("form.sharesFromRun")}</h3>
                        <RecipeDetails recipe={repeat.snapshot} workspace={workspace} sets={sets} />
                    </>
                ) : (
                    <>
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <h3 id={`${id}-shares`} className="text-sm font-medium">{t("form.shares")}</h3>
                            <Button type="button" size="sm" variant="outline" disabled={!available.length} data-testid="weights-even"
                                onClick={() => update({ weights: evenWeights(shares) })}>
                                {t("form.evenly")}
                            </Button>
                        </div>
                        <p className="text-sm text-muted-foreground">{t("form.sharesHint")}</p>
                        <div className="space-y-4">
                            {shares.map(share => (
                                <div key={share.variant.id} className="space-y-2" data-generation-variant-id={share.variant.id} data-eligible={share.eligible || undefined}>
                                    <div className="flex items-center justify-between gap-3">
                                        <label htmlFor={`${id}-weight-${share.variant.id}`} className="min-w-0 text-sm break-words">
                                            {share.variant.name}
                                            <span className="ml-2 text-xs text-muted-foreground" data-testid="variant-classes">
                                                {share.eligible ? classesLabel(share.classes) : generationTexts.variantWithoutAssets}
                                            </span>
                                        </label>
                                        <div className="flex shrink-0 items-center gap-2">
                                            <Input id={`${id}-weight-${share.variant.id}`} type="number" min={0} max={100} step={1} disabled={!share.eligible}
                                                className="w-20 text-right tabular-nums" data-testid="weight-input" value={share.percentage}
                                                onChange={event => setWeight(share.variant.id, clampPercent(event.target.value))} />
                                            <span className="text-sm text-muted-foreground">%</span>
                                        </div>
                                    </div>
                                    <Slider thumbLabels={[t("form.variantShare", { name: share.variant.name })]} disabled={!share.eligible} value={[share.percentage]} max={100} step={1}
                                        onValueChange={next => setWeight(share.variant.id, next[0])} />
                                </div>
                            ))}
                            {workspace && !shares.length && <p className="text-sm text-muted-foreground">{t("form.noVariants")}</p>}
                        </div>
                        <p className={`text-sm tabular-nums ${totalValid ? "text-muted-foreground" : "text-destructive"}`} aria-live="polite" data-testid="weights-total"
                            data-invalid={!totalValid || undefined}>
                            {t("form.total", { total: formatNumber(total) })}
                        </p>
                        {errorOf("variantWeights", "classVariantIds") && <p className="text-xs text-destructive" role="alert" data-testid="weights-error">{errorOf("variantWeights", "classVariantIds")}</p>}
                    </>
                )}
            </section>
            <p className="text-xs text-muted-foreground">
                <Trans t={t} i18nKey="form.canvasNote" values={{ width: canvas.width, height: canvas.height }} components={{ size: <span data-testid="recipe-canvas" /> }} />
            </p>
            <div className="grid grid-cols-2 gap-3">
                <FormField label={t("form.objects")} htmlFor={`${id}-objects`}>
                    <Input id={`${id}-objects`} type="number" min={0} step={1} data-testid="recipe-objects" aria-invalid={!Number.isSafeInteger(values.objects) || values.objects < 0 || undefined}
                        value={Number.isFinite(values.objects) ? values.objects : ""} onChange={event => update({ objects: event.target.value === "" ? Number.NaN : Number(event.target.value) })} />
                </FormField>
                <FormField label={t("form.scale")} htmlFor={`${id}-scale`}>
                    <Input id={`${id}-scale`} type="number" min={0} step="any" data-testid="recipe-scale" aria-invalid={!(values.scale > 0) || undefined}
                        value={Number.isFinite(values.scale) ? values.scale : ""} onChange={event => update({ scale: event.target.value === "" ? Number.NaN : Number(event.target.value) })} />
                </FormField>
            </div>
            <p className={`text-xs ${hint.invalid || errorOf("objectsPerImage", "scale") ? "text-destructive" : "text-muted-foreground"}`} aria-live="polite" data-testid="objects-hint">
                {errorOf("objectsPerImage", "scale") ?? hint.text}
            </p>
            <div className="space-y-2">
                <div className="flex items-center justify-between gap-3">
                    <label htmlFor={`${id}-empty`} className="text-xs text-muted-foreground">{t("form.empty")}</label>
                    <div className="flex shrink-0 items-center gap-2">
                        <Input id={`${id}-empty`} type="number" min={0} max={100} step="any" className="w-20 text-right tabular-nums" data-testid="recipe-empty"
                            value={values.emptyPercent} onChange={event => update({ emptyPercent: clampPercent(event.target.value) })} />
                        <span className="text-sm text-muted-foreground">%</span>
                    </div>
                </div>
                <Slider thumbLabels={[t("form.empty")]} value={[values.emptyPercent]} max={100} step={1} onValueChange={next => update({ emptyPercent: next[0] })} />
                <p className={`text-xs ${errorOf("emptyProbability") ? "text-destructive" : "text-muted-foreground"}`}>{errorOf("emptyProbability") ?? generationTexts.emptyHint}</p>
            </div>
            <GenerationSamples samples={samples} palette={palette} stale={samplesStale} />
            <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" disabled={busy || samples.loading || !!invalidField} data-testid="recipe-samples" onClick={requestSamples}>
                    {t("form.sample")}
                </Button>
                <Button type="submit" disabled={busy || !!invalidField} data-testid="recipe-save">{t("form.save")}</Button>
                {mode.kind !== "create" && <Button type="button" variant="ghost" disabled={busy} data-testid="recipe-cancel" onClick={cancel}>{t("form.cancel")}</Button>}
            </div>
        </form>
    );
}

function FormField({ label, htmlFor, error, children }: { label: string; htmlFor: string; error?: string; children: ReactNode }) {
    return (
        <div className="space-y-2">
            <label htmlFor={htmlFor} className="block text-xs text-muted-foreground">{label}</label>
            {children}
            {error && <p className="text-xs text-destructive" role="alert">{error}</p>}
        </div>
    );
}
