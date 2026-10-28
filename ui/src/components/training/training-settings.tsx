import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Loader2 } from "lucide-react";
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
import { formatDate } from "@/lib/format";
import { formFromParameters, formTunedDefaults, parseTrainingForm, sameParameters, type TrainingForm } from "@/lib/training/training-form";
import { count } from "@/lib/training/run-view";
import { TrainingParameterField, TrainingParameterGroups, type ParameterFieldContext } from "./training-parameter-fields";
import { TrainingPresetBar } from "./training-preset-bar";

const mainKeys = ["epochs", "batch", "lr0", "device"] as const;
const advancedKeys = ["warmupEpochs", "seed"] as const;

/**
 * Вкладка «Настройки»: пустое состояние без датасетов, среда worker (не настроена или не найдена — понятное сообщение),
 * «Новый запуск обучения» — датасет с составом, классами и предупреждениями, пресеты, основные поля, «Дополнительно», группы
 * параметров, подпись об архитектуре и очереди, «Начать обучение» / «Поставить в очередь».
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
    /** Поле из details.field ответа host — подсвечивается. */
    hostField: string | undefined;
    start: (datasetVersionId: string, parameters: TrainingParameters, presetName: string | null) => void;
    savePreset: (name: string, parameters: TrainingParameters) => Promise<string | null>;
    deletePreset: (preset: TrainingPresetResponseDto) => Promise<boolean>;
    openDatasets: () => void;
}) {
    const [chosenDatasetId, setDatasetId] = useState("");
    const [form, setForm] = useState<TrainingForm>(() => formFromParameters());
    const [presetId, setPresetId] = useState("");
    const deviceChosen = useRef(false);
    const available = useMemo(() => datasets.filter(item => !item.archivedAt), [datasets]);
    // Без выбора (или выбранный ушёл в архив) — новейший датасет.
    const dataset = available.find(item => item.id === chosenDatasetId) ?? available[0];
    const datasetId = dataset?.id ?? "";

    // Устройство по умолчанию — первая видеокарта, без них процессор; выбор пользователя или пресета не перезаписывается.
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
    // Форма изменена после выбора пресета: выбор снят (его можно выбрать снова), запуск идёт без имени пресета.
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
                <h2 className="font-medium">Сначала соберите датасет</h2>
                <p className="max-w-prose text-sm text-muted-foreground">
                    Обучение запускается на неизменяемой версии датасета проекта. Готовых датасетов пока нет: разметьте изображения и соберите датасет.
                </p>
                <Button variant="outline" data-testid="training-open-datasets" onClick={openDatasets}>Открыть датасеты</Button>
            </section>
        );
    }

    return (
        <div className="space-y-4">
            {runtimeProblem && (
                <Alert variant="destructive" data-testid="training-runtime-problem" data-code={runtimeProblem.code ?? undefined}>
                    <AlertDescription>
                        <p className="font-medium text-foreground">Среда обучения недоступна</p>
                        <p>{runtimeProblem.message ?? "Python worker не настроен."}</p>
                    </AlertDescription>
                </Alert>
            )}
            <form className="space-y-5 rounded-lg border bg-card p-5" onSubmit={submit} aria-label="Новый запуск обучения" data-testid="training-form">
                <h2 className="font-medium">Новый запуск обучения</h2>
                <label className="block space-y-1">
                    <span className="block text-xs text-muted-foreground">Датасет</span>
                    <Select value={datasetId} onValueChange={id => { if (id) setDatasetId(id); }}disabled={busy || !available.length}>
                        <SelectTrigger aria-label="Датасет" className="w-full sm:w-96" data-testid="training-dataset">
                            <SelectValue placeholder={datasetsLoading ? "Загрузка…" : "Выберите датасет"} />
                        </SelectTrigger>
                        <SelectContent>
                            {available.map(item => (
                                <SelectItem key={item.id} value={item.id}>{item.name} · {new Date(item.createdAt).toLocaleDateString("ru-RU")}</SelectItem>
                            ))}
                        </SelectContent>
                    </Select>
                </label>
                {dataset && (
                    <div className="space-y-1 rounded-md border p-3 text-xs text-muted-foreground" data-testid="training-dataset-summary" data-dataset-id={dataset.id}>
                        <p data-testid="training-dataset-counts">
                            Train {count(dataset.counts.train)} · Validation {count(dataset.counts.val)} · Test {count(dataset.counts.test)} · {dataset.width} × {dataset.height}
                        </p>
                        <p data-testid="training-dataset-classes">
                            Классы ({dataset.classMap.length}): {dataset.classMap.map(entry => entry.name).join(", ")}
                        </p>
                        <p>Собран {formatDate(dataset.createdAt)} · рамок {count(dataset.composition.boxes)} · негативов {count(dataset.composition.negatives)}</p>
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
                        Пустые поля «Авто» показывают значение, которое подберёт обучение. Фактическая точность и размер пакета попадут в паспорт.
                    </p>
                </div>
                <details className="rounded-md border px-3 py-2" data-testid="training-advanced">
                    <summary className="cursor-pointer select-none text-sm text-muted-foreground">Дополнительно</summary>
                    <div className="mt-3 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">{advancedKeys.map(field)}</div>
                    <p className="mt-2 text-xs text-muted-foreground">
                        Пустой разогрев подбирается по батчу и числу эпох (не больше четверти прогона). Seed делает запуск воспроизводимым.
                    </p>
                </details>
                <TrainingParameterGroups context={context} />
                <p className={`min-h-4 text-xs ${valid ? "text-muted-foreground" : "text-destructive"}`} aria-live="polite" data-testid="training-form-hint">
                    {valid
                        ? "Фактические параметры обучения попадут в паспорт версии."
                        : `Проверьте параметры: ${parsed.invalid.map(key => trainingParameter(key)?.label ?? key).join(", ")}.`}
                </p>
                <p className="text-xs text-muted-foreground" data-testid="training-architecture-note">
                    Архитектура {project.baseModelId}, вход {project.inputSize.width} × {project.inputSize.height}. Одновременно обучается один запуск; следующие
                    ждут освобождения видеокарты в очереди. Успешный запуск создаёт новую версию модели, прежние версии не меняются.
                </p>
                <Button type="submit" disabled={busy || !dataset || !valid || blocked || !!runtimeProblem} data-testid="training-start">
                    {busy && <Loader2 className="animate-spin" aria-hidden />}
                    {hasActive ? "Поставить в очередь" : "Начать обучение"}
                </Button>
            </form>
        </div>
    );
}
