import type { FormEvent, RefObject } from "react";
import { Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import { datasetTexts, maxDatasetNameLength, randomDatasetSeed, type DatasetSpecificationField } from "@/api/projects/datasets/dataset-rules";
import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Slider } from "@/components/ui/slider";
import { clampPercent, datasetFieldTexts, parseSeed, validationPercent, type DatasetFormValues } from "@/lib/datasets/dataset-form";
import type { ReviewCount } from "@/lib/use-group-review-counts";
import { DatasetGroupPicker } from "./dataset-group-picker";

export interface DatasetFieldError {
    field: DatasetSpecificationField;
    message: string;
}

const toggle = (values: readonly string[], id: string) => values.includes(id) ? values.filter(value => value !== id) : [...values, id];

/**
 * "Prepare a build": the name, groups with frame counts and the reviewed share, classes with "Select all" / "Clear all", Train and
 * Test shares with the Validation remainder, "Advanced" — seed, "New random seed", empty frames and unfinished generations; "Check
 * before building" is unavailable while the form is wrong (the same rules as the host). fieldError — the field from details.field of the
 * host answer.
 */
export function DatasetBuildForm({ values, setValues, groups, reviewCount, workspace, invalidField, fieldError, busy, planning, plan, nameRef, repeatNotice }: {
    values: DatasetFormValues;
    setValues: (update: (current: DatasetFormValues) => DatasetFormValues) => void;
    groups: readonly ImageGroupResponseDto[];
    reviewCount: (groupId: string) => ReviewCount | undefined;
    workspace: CatalogWorkspaceResponseDto | undefined;
    invalidField: DatasetSpecificationField | null;
    fieldError: DatasetFieldError | undefined;
    busy: boolean;
    planning: boolean;
    plan: () => void;
    nameRef: RefObject<HTMLInputElement | null>;
    repeatNotice: boolean;
}) {
    const { t } = useTranslation("datasets");
    const classes = workspace?.classes ?? [];
    const activeClassIds = classes.filter(item => !item.archivedAt).map(item => item.id);
    const missingClasses = values.classIds.filter(id => !activeClassIds.includes(id));
    const val = validationPercent(values);
    const seed = parseSeed(values.seed);
    const validSeed = Number.isSafeInteger(seed) && seed >= 0 && seed <= 4_294_967_295;
    const hostField = fieldError?.field;
    const shown = (field: DatasetSpecificationField) => hostField === field;

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!busy && !invalidField) plan();
    }

    return (
        <form className="space-y-4 rounded-md border bg-card p-5" onSubmit={submit} data-testid="dataset-build-form" aria-label={t("build.title")}>
            <h2 className="font-medium">{t("build.title")}</h2>
            {repeatNotice && (
                <p className="text-xs text-muted-foreground" data-testid="dataset-repeat-notice">
                    {t("build.repeatNotice")}
                </p>
            )}
            <label className="block space-y-2">
                <span className="text-xs text-muted-foreground">{t("build.name")}</span>
                <Input ref={nameRef} required maxLength={maxDatasetNameLength} data-testid="dataset-name" aria-invalid={hostField === "name" || undefined}
                    value={values.name} onChange={event => setValues(current => ({ ...current, name: event.target.value }))} />
            </label>
            {hostField === "name" && <FieldHint text={fieldError!.message} />}
            <div className="grid gap-5 md:grid-cols-2">
                <div className="space-y-1">
                    <DatasetGroupPicker groups={groups} selected={values.groupIds} reviewCount={reviewCount} invalid={hostField === "groupIds"}
                        toggle={id => setValues(current => ({ ...current, groupIds: toggle(current.groupIds, id) }))} />
                    {hostField === "groupIds" && <FieldHint text={fieldError!.message} />}
                </div>
                <div className="space-y-1">
                    <fieldset className="max-h-64 min-h-32 space-y-2 overflow-y-auto rounded-md border p-3" data-testid="dataset-classes">
                        <legend className="mb-2 text-xs text-muted-foreground">{t("build.classes")}</legend>
                        <div className="flex flex-wrap gap-2 pb-1">
                            <Button type="button" size="sm" variant="outline" data-testid="dataset-classes-all"
                                disabled={!activeClassIds.length || activeClassIds.every(id => values.classIds.includes(id))}
                                onClick={() => setValues(current => ({ ...current, classIds: [...activeClassIds] }))}>
                                {t("build.selectAll")}
                            </Button>
                            <Button type="button" size="sm" variant="outline" data-testid="dataset-classes-none" disabled={!values.classIds.length}
                                onClick={() => setValues(current => ({ ...current, classIds: [] }))}>
                                {t("build.clearAll")}
                            </Button>
                        </div>
                        {classes.filter(item => !item.archivedAt).map(item => (
                            <label key={item.id} className="flex items-center gap-2 text-sm" data-dataset-class-id={item.id}>
                                <Checkbox checked={values.classIds.includes(item.id)} aria-label={t("build.classLabel", { name: item.name })}
                                    onCheckedChange={() => setValues(current => ({ ...current, classIds: toggle(current.classIds, item.id) }))} />
                                {item.name}
                            </label>
                        ))}
                        {missingClasses.map(id => (
                            <label key={id} className="flex items-center gap-2 text-sm text-muted-foreground" data-dataset-class-id={id}>
                                <Checkbox checked aria-label={t("build.unavailableClass")} onCheckedChange={() => setValues(current => ({ ...current, classIds: toggle(current.classIds, id) }))} />
                                {t("build.classUnavailable", { name: classes.find(item => item.id === id)?.name ?? t("build.classDeleted") })}
                            </label>
                        ))}
                        {!activeClassIds.length && !missingClasses.length && <p className="text-sm text-muted-foreground">{t("build.noClasses")}</p>}
                    </fieldset>
                    <p className="text-xs text-muted-foreground">{t("build.classOrder")}</p>
                    {(hostField === "classIds" || hostField === "variants" || hostField === "roles") && <FieldHint text={fieldError!.message} />}
                </div>
            </div>
            <div className="grid gap-5 sm:grid-cols-2">
                <SplitSlider id="dataset-train" label={t("build.train")} value={values.trainPercent} min={1}
                    onChange={value => setValues(current => ({ ...current, trainPercent: Math.min(99 - current.testPercent, Math.max(1, clampPercent(value))) }))} />
                <SplitSlider id="dataset-test" label={t("build.test")} value={values.testPercent} min={0}
                    onChange={value => setValues(current => ({ ...current, testPercent: Math.min(99 - current.trainPercent, clampPercent(value)) }))} />
            </div>
            <p className={`min-h-5 text-xs ${val <= 0 || shown("ratios") ? "text-destructive" : "text-muted-foreground"}`} role={val <= 0 ? "alert" : undefined}
                data-testid="dataset-validation-hint">
                {val <= 0 ? datasetTexts.ratiosInvalid : hostField === "ratios" ? fieldError!.message : datasetTexts.validationRemainder(val)}
            </p>
            <details className="rounded-md border px-3 py-2" open={shown("seed") || undefined} data-testid="dataset-advanced">
                <summary className="cursor-pointer text-sm text-muted-foreground select-none">{t("build.advanced")}</summary>
                <div className="mt-3 grid gap-3 sm:grid-cols-2">
                    <label className="block space-y-2">
                        <span className="text-xs text-muted-foreground">{t("build.seed")}</span>
                        <Input inputMode="numeric" data-testid="dataset-seed" aria-invalid={!validSeed || undefined} value={values.seed}
                            onChange={event => setValues(current => ({ ...current, seed: event.target.value }))} />
                    </label>
                </div>
                <p className={`mt-2 text-xs ${validSeed && hostField !== "seed" ? "text-muted-foreground" : "text-destructive"}`} aria-live="polite" data-testid="dataset-seed-hint">
                    {!validSeed ? datasetTexts.seedInvalid : hostField === "seed" ? fieldError!.message : datasetTexts.seedHint}
                </p>
                <Button type="button" variant="outline" size="sm" className="mt-3" data-testid="dataset-new-seed"
                    onClick={() => setValues(current => ({ ...current, seed: String(randomDatasetSeed()) }))}>
                    {t("build.newSeed")}
                </Button>
                <div className="mt-3 space-y-2">
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox checked={values.includeReviewedEmpty} data-testid="dataset-include-empty"
                            onCheckedChange={value => setValues(current => ({ ...current, includeReviewedEmpty: value === true }))} />
                        {t("build.includeEmpty")}
                    </label>
                    <label className="flex items-center gap-2 text-sm">
                        <Checkbox checked={values.allowPartialGeneration} data-testid="dataset-allow-partial"
                            onCheckedChange={value => setValues(current => ({ ...current, allowPartialGeneration: value === true }))} />
                        {t("build.allowPartial")}
                    </label>
                </div>
            </details>
            <p className="text-xs text-muted-foreground">
                {t("build.note")}
            </p>
            <div className="flex flex-wrap items-center gap-3">
                <Button type="submit" data-testid="dataset-plan" disabled={busy || !!invalidField || !workspace}>
                    {planning && <Loader2 className="animate-spin" aria-hidden />}{t("build.plan")}
                </Button>
                {invalidField && invalidField !== "ratios" && invalidField !== "seed" && (
                    <p className="text-xs text-muted-foreground" data-testid="dataset-form-hint">{datasetFieldTexts[invalidField]}</p>
                )}
            </div>
        </form>
    );
}

function FieldHint({ text }: { text: string }) {
    return <p className="text-xs text-destructive" role="alert" data-testid="dataset-field-error">{text}</p>;
}

function SplitSlider({ id, label, value, min, onChange }: { id: string; label: string; value: number; min: number; onChange: (value: number) => void }) {
    return (
        <div className="space-y-2">
            <div className="flex items-center justify-between gap-3">
                <label htmlFor={id} className="text-xs text-muted-foreground">{label}</label>
                <div className="flex shrink-0 items-center gap-2">
                    <Input id={id} data-testid={id} type="number" required min={min} max={99} step={1} className="w-20 text-right tabular-nums" value={value}
                        onChange={event => onChange(Number(event.target.value))} />
                    <span className="text-sm text-muted-foreground">%</span>
                </div>
            </div>
            <Slider aria-label={label} value={[value]} min={min} max={99} step={1} onValueChange={values => onChange(values[0])} />
        </div>
    );
}

