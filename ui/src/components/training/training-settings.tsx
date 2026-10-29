import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { TrainingParameters } from "@/api/projects/training/request-dto/training-request-dto";
import type {
    GpuInventoryResponseDto,
    TrainingPresetResponseDto,
    TrainingRuntimeResponseDto,
} from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { defaultDevice, trainingFormWarnings, trainingParameter } from "@/api/projects/training/training-parameters";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { formatDate, formatDay } from "@/lib/format";
import { formFromParameters, formTunedDefaults, parseTrainingForm, sameParameters, type TrainingForm } from "@/lib/training/training-form";
import { count } from "@/lib/training/run-view";
import { TrainingParameterField, TrainingParameterGroups, type ParameterFieldContext } from "./training-parameter-fields";
import { TrainingPresetBar } from "./training-preset-bar";
import { errorText } from "@/lib/errors";

const mainKeys = ["epochs", "batch", "lr0", "device"] as const;
const advancedKeys = ["warmupEpochs", "seed"] as const;

/**
 * The tab "Settings": the empty state without datasets, the worker environment (not configured or not found — a clear message),
 * "New training run" — the dataset with its composition, classes and warnings, presets, main fields, "Advanced", parameter groups,
 * a note about the architecture and the queue, "Start training" / "Queue".
 */
export function TrainingSettings({ project, datasets, datasetsLoading, presets, runtime, gpus, hasActive, busy, hostField, start, savePreset, deletePreset, openDatasets }: {
    project: ProjectResponseDto;
    datasets: readonly DatasetVersionResponseDto[];
    datasetsLoading: boolean;
    presets: readonly TrainingPresetResponseDto[];
    runtime: TrainingRuntimeResponseDto | null | undefined;
    gpus: GpuInventoryResponseDto | null | undefined;
    hasActive: boolean;
    busy: boolean;
    /** The field from details.field of the host answer — highlighted. */
    hostField: string | undefined;
    start: (datasetVersionId: string, parameters: TrainingParameters, presetName: string | null) => void;
    savePreset: (name: string, parameters: TrainingParameters) => Promise<string | null>;
    deletePreset: (preset: TrainingPresetResponseDto) => Promise<boolean>;
    openDatasets: () => void;
}) {
    const { t } = useTranslation("training");
    const [chosenDatasetId, setDatasetId] = useState("");
    const [form, setForm] = useState<TrainingForm>(() => formFromParameters());
    const [presetId, setPresetId] = useState("");
    const deviceChosen = useRef(false);
    const available = useMemo(() => datasets.filter(item => !item.archivedAt), [datasets]);
    // Without a choice (or when the chosen one went to the archive) — the newest dataset.
    const dataset = available.find(item => item.id === chosenDatasetId) ?? available[0];
    const datasetId = dataset?.id ?? "";

    // The default device is the first GPU, without GPUs the processor; a choice of the user or a preset is not overwritten.
    useEffect(() => {
        if (gpus === undefined || deviceChosen.current) return;
        setForm(current => ({ ...current, device: defaultDevice(gpus) }));
    }, [gpus]);

    useEffect(() => {
        if (presetId && !presets.some(item => item.id === presetId)) setPresetId("");
    }, [presets, presetId]);

    const parsed = parseTrainingForm(form);
    const invalid = new Set(parsed.invalid);
    if (hostField && trainingParameter(hostField)) invalid.add(hostField);
    const valid = parsed.invalid.length === 0;
    const warnings = dataset ? trainingFormWarnings(dataset, parsed.parameters) : [];
    const blocked = warnings.some(warning => warning.blocking);
    const runtimeProblem = runtime && !runtime.available ? runtime : null;
    const preset = presets.find(item => item.id === presetId);
    // The form changed after a preset was chosen: the choice is dropped (it can be chosen again), the run goes without the preset name.
    const presetChanged = !!preset && !sameParameters(parsed.parameters, preset.parameters);

    const context: ParameterFieldContext = {
        form,
        update: next => {
            setForm(current => {
                const updated = next(current);
                if (updated.device !== current.device) deviceChosen.current = true;
                return updated;
            });
        },
        invalid,
        tuned: formTunedDefaults(form),
        gpus,
        disabled: busy,
    };
    const field = (key: string) => <TrainingParameterField key={key} parameter={trainingParameter(key)!} context={context} />;

    function applyPreset(id: string) {
        const selected = presets.find(item => item.id === id);
        if (!selected) return;
        setPresetId(id);
        deviceChosen.current = true;
        setForm(formFromParameters(selected.parameters));
    }

    function submit(event: FormEvent) {
        event.preventDefault();
        if (!dataset || !valid || blocked || busy || runtimeProblem) return;
        start(dataset.id, parsed.parameters, preset && !presetChanged ? preset.name : null);
    }

    if (!datasetsLoading && !available.length) {
        return (
            <section className="space-y-3 rounded-lg border border-dashed p-6" data-testid="training-empty">
                <h2 className="font-medium">{t("settings.emptyTitle")}</h2>
                <p className="max-w-prose text-sm text-muted-foreground">{t("settings.emptyText")}</p>
                <Button variant="outline" data-testid="training-open-datasets" onClick={openDatasets}>{t("settings.openDatasets")}</Button>
            </section>
        );
    }

    return (
        <div className="space-y-4">
            {runtimeProblem && (
                <Alert variant="destructive" data-testid="training-runtime-problem" data-code={runtimeProblem.code ?? undefined}>
                    <AlertDescription>
                        <p className="font-medium text-foreground">{t("settings.runtimeUnavailable")}</p>
                        <p>{runtimeProblem.code ? errorText(runtimeProblem.code) : t("settings.runtimeNotConfigured")}</p>
                    </AlertDescription>
                </Alert>
            )}
            <form className="space-y-5 rounded-lg border bg-card p-5" onSubmit={submit} aria-label={t("settings.newRun")} data-testid="training-form">
                <h2 className="font-medium">{t("settings.newRun")}</h2>
                <label className="block space-y-1">
                    <span className="block text-xs text-muted-foreground">{t("settings.dataset")}</span>
                    <Select value={datasetId} onValueChange={id => { if (id) setDatasetId(id); }}disabled={busy || !available.length}>
                        <SelectTrigger aria-label={t("settings.dataset")} className="w-full sm:w-96" data-testid="training-dataset">
                            <SelectValue placeholder={datasetsLoading ? t("settings.loading") : t("settings.chooseDataset")} />
                        </SelectTrigger>
                        <SelectContent>
                            {available.map(item => (
                                <SelectItem key={item.id} value={item.id}>{item.name} · {formatDay(item.createdAt)}</SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                </label>
                {dataset && (
                    <div className="space-y-1 rounded-md border p-3 text-xs text-muted-foreground" data-testid="training-dataset-summary" data-dataset-id={dataset.id}>
                        <p data-testid="training-dataset-counts">
                            {t("settings.counts", { train: count(dataset.counts.train), val: count(dataset.counts.val), test: count(dataset.counts.test), width: dataset.width, height: dataset.height })}
                        </p>
                        <p data-testid="training-dataset-classes">
                            {t("settings.classes", { number: dataset.classMap.length, names: dataset.classMap.map(entry => entry.name).join(", ") })}
                        </p>
                        <p>{t("settings.built", { date: formatDate(dataset.createdAt), boxes: count(dataset.composition.boxes), negatives: count(dataset.composition.negatives) })}</p>
                        {warnings.map(warning => (
                            <p key={warning.code} role={warning.blocking ? "alert" : undefined} data-warning={warning.code}
                                className={warning.blocking ? "text-destructive" : "text-amber-700 dark:text-amber-400"}>
                                {warning.message}
                            </p>
                        ))}
                    </div>
                )}
                <TrainingPresetBar
                    presets={presets}
                    presetId={presetId}
                    changed={presetChanged}
                    busy={busy}
                    canSave={valid}
                    apply={applyPreset}
                    save={async name => {
                        const id = await savePreset(name, parsed.parameters);
                        if (id) setPresetId(id);
                        return id !== null;
                    }}
                    remove={async item => {
                        const done = await deletePreset(item);
                        if (done) setPresetId("");
                        return done;
                    }}
                />
                <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4" data-testid="training-main-fields">{mainKeys.map(field)}</div>
                <div className="grid gap-4 sm:grid-cols-2">
                    {field("amp")}
                    <p className="self-end text-xs text-muted-foreground">
                        {t("settings.autoNote")}
                    </p>
                </div>
                <details className="rounded-md border px-3 py-2" data-testid="training-advanced">
                    <summary className="cursor-pointer select-none text-sm text-muted-foreground">{t("settings.advanced")}</summary>
                    <div className="mt-3 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">{advancedKeys.map(field)}</div>
                    <p className="mt-2 text-xs text-muted-foreground">
                        {t("settings.advancedNote")}
                    </p>
                </details>
                <TrainingParameterGroups context={context} />
                <p className={`min-h-4 text-xs ${valid ? "text-muted-foreground" : "text-destructive"}`} aria-live="polite" data-testid="training-form-hint">
                    {valid
                        ? t("settings.validHint")
                        : t("settings.invalidHint", { names: parsed.invalid.map(key => trainingParameter(key)?.label ?? key).join(", ") })}
                </p>
                <p className="text-xs text-muted-foreground" data-testid="training-architecture-note">
                    {t("settings.architectureNote", { architecture: project.baseModelId, width: project.inputSize.width, height: project.inputSize.height })}
                </p>
                <Button type="submit" disabled={busy || !dataset || !valid || blocked || !!runtimeProblem} data-testid="training-start">
                    {busy && <Loader2 className="animate-spin" aria-hidden />}
                    {hasActive ? t("settings.queue") : t("settings.start")}
                </Button>
            </form>
        </div>
    );
}
