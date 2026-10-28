import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import {
    deviceOptions,
    isLr0ChosenByOptimizer,
    trainingParameterGroups,
    trainingParameters,
    unavailableTrainingParameters,
    type TrainingParameterDefinition,
} from "@/api/projects/training/training-parameters";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
    choiceFromKey,
    choiceKey,
    formatParameterNumber,
    isToggleOn,
    setToggle,
    type TrainingForm,
} from "@/lib/training/training-form";

export interface ParameterFieldContext {
    form: TrainingForm;
    update: (update: (current: TrainingForm) => TrainingForm) => void;
    invalid: ReadonlySet<string>;
    /** Значения «Авто» полей, которые посчитает worker. */
    tuned: { lr0: number; warmupEpochs: number; patience: number; workers: number };
    gpus: GpuInventoryResponseDto | null | undefined;
    disabled: boolean;
}

const lr0ByOptimizer = "Подберёт оптимизатор";

function autoPlaceholder(parameter: TrainingParameterDefinition, context: ParameterFieldContext): string | undefined {
    if (parameter.defaultValue !== null) return undefined;
    const tuned = (context.tuned as Record<string, number>)[parameter.key];
    return tuned === undefined ? "Авто" : `Авто: ${formatParameterNumber(tuned)}`;
}

/**
 * Поле параметра обучения по реестру: флажок, выбор (с «Авто» у необязательных), «Устройство» из списка видеокарт (без драйвера —
 * «Процессор» и ручной ввод), число или список текстом с фактическим значением «Авто» в подсказке поля, переключатель «Повороты»
 * перед полем градусов. lr0 при оптимизаторе auto недоступно и помечено «Подберёт оптимизатор».
 */
export function TrainingParameterField({ parameter, context }: { parameter: TrainingParameterDefinition; context: ParameterFieldContext }) {
    const { form, update, invalid, disabled } = context;
    const value = form[parameter.key];
    const isInvalid = invalid.has(parameter.key);
    const hintId = `training-parameter-${parameter.key}-hint`;
    const set = (next: TrainingForm[string]) => update(current => ({ ...current, [parameter.key]: next }));
    // Имя Ultralytics мелким шрифтом, если подпись его ещё не называет («Размер пакета (batch)»).
    const caption = parameter.label.includes(`(${parameter.ultralyticsKey})`)
        ? null
        : <span className="ml-1 font-mono text-[11px] text-muted-foreground">{parameter.ultralyticsKey}</span>;

    if (parameter.type === "boolean") {
        return (
            <div className="min-w-0 space-y-1" data-training-parameter={parameter.key}>
                <label className="flex items-start gap-2 text-sm">
                    <Checkbox checked={value === true} disabled={disabled} aria-label={parameter.label} aria-describedby={parameter.hint ? hintId : undefined}
                        onCheckedChange={checked => set(checked === true)} />
                    <span>{parameter.label}{caption}</span>
                </label>
                {parameter.hint && <p id={hintId} className="text-xs leading-relaxed text-muted-foreground">{parameter.hint}</p>}
            </div>
        );
    }

    let control;
    let note: string | undefined;
    let toggled = true;
    if (parameter.type === "select") {
        control = (
            <Select value={choiceKey(value)} disabled={disabled} onValueChange={next => { if (next) set(choiceFromKey(next)); }}>
                <SelectTrigger className="w-full" aria-label={parameter.label} aria-invalid={isInvalid || undefined} aria-describedby={parameter.hint ? hintId : undefined}
                    data-testid={`training-field-${parameter.key}`}>
                    <SelectValue />
                </SelectTrigger>
                <SelectContent>
                    {parameter.nullable && <SelectItem value={choiceKey(null)}>Авто</SelectItem>}
                    {(parameter.choices ?? []).map(choice => <SelectItem key={choiceKey(choice.value)} value={choiceKey(choice.value)}>{choice.label}</SelectItem>)}
                </SelectContent>
            </Select>
        );
    } else if (parameter.type === "device") {
        const { options, manualEntry } = deviceOptions(context.gpus ?? null);
        const device = typeof value === "string" ? value : "";
        const listed = options.some(option => option.value === device);
        // The value always has its own item: inside a form Radix resets a value missing from the options to "".
        control = (
            <div className="space-y-2">
                <Select value={device} disabled={disabled} onValueChange={next => { if (next) set(next); }}>
                    <SelectTrigger className="w-full" aria-label={parameter.label} aria-invalid={isInvalid || undefined} data-testid="training-field-device">
                        <SelectValue placeholder="Выберите устройство" />
                    </SelectTrigger>
                    <SelectContent>
                        {options.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                        {device && !listed && (
                            <SelectItem value={device}>{context.gpus === undefined ? `Видеокарта ${device} (поиск видеокарт…)` : `Вручную: ${device}`}</SelectItem>
                        )}
                    </SelectContent>
                </Select>
                {manualEntry && context.gpus !== undefined && (
                    <Input aria-label="Устройство вручную" placeholder="cpu, −1 или номер видеокарты" disabled={disabled} data-testid="training-device-manual"
                        value={listed ? "" : device} aria-invalid={isInvalid || undefined} onChange={event => set(event.target.value.trim())} />
                )}
            </div>
        );
        if (manualEntry && context.gpus !== undefined) note = "Драйвер NVIDIA не найден: доступен процессор или номер видеокарты вручную.";
    } else {
        const byOptimizer = parameter.key === "lr0" && isLr0ChosenByOptimizer({ optimizer: form.optimizer as string });
        toggled = isToggleOn(parameter, form);
        control = (
            <Input
                inputMode={parameter.type === "list" ? "text" : "decimal"}
                aria-label={parameter.label}
                aria-invalid={isInvalid || undefined}
                aria-describedby={parameter.hint ? hintId : undefined}
                data-testid={`training-field-${parameter.key}`}
                disabled={disabled || byOptimizer || !toggled}
                placeholder={byOptimizer ? lr0ByOptimizer : autoPlaceholder(parameter, context)}
                value={byOptimizer ? "" : String(value ?? "")}
                onChange={event => set(event.target.value)}
            />
        );
        if (byOptimizer) note = lr0ByOptimizer;
    }

    return (
        <div className="min-w-0 space-y-1" data-training-parameter={parameter.key} data-invalid={isInvalid || undefined}>
            {parameter.toggle && (
                <label className="flex items-center gap-2 text-sm">
                    <Checkbox checked={toggled} disabled={disabled} aria-label={parameter.toggle.label} data-testid={`training-toggle-${parameter.key}`}
                        onCheckedChange={checked => update(current => setToggle(current, parameter.key, checked === true))} />
                    {parameter.toggle.label}
                </label>
            )}
            <label className="block space-y-1">
                <span className="flex min-h-8 items-end text-xs text-muted-foreground"><span className="line-clamp-2">{parameter.label}{caption}</span></span>
                {control}
            </label>
            {note && <p className="text-xs font-medium text-amber-700 dark:text-amber-400" data-testid={`training-note-${parameter.key}`}>{note}</p>}
            {parameter.hint && <p id={hintId} className="text-xs leading-relaxed text-muted-foreground">{parameter.hint}</p>}
            {isInvalid && <p role="alert" className="text-xs text-destructive">Проверьте значение параметра.</p>}
        </div>
    );
}

/**
 * Группы параметров вне основных полей (раскрываются по одной, «Аугментации» открыта) и «Управление
 * приложением и недоступные настройки» с причинами.
 */
export function TrainingParameterGroups({ context }: { context: ParameterFieldContext }) {
    return (
        <div className="space-y-3" data-testid="training-parameter-groups">
            <p className="text-xs text-muted-foreground">Обнаружение объектов. Настройки сохраняются в пресет и снимок запуска.</p>
            {trainingParameterGroups.filter(group => group.id !== "system").map(group => {
                const fields = trainingParameters.filter(parameter => parameter.group === group.id && !parameter.base);
                if (!fields.length) return null;
                const invalid = fields.some(parameter => context.invalid.has(parameter.key));
                return (
                    <details key={group.id} className="rounded-md border px-4 py-3" open={group.id === "augmentation" || invalid || undefined} data-training-group={group.id}>
                        <summary className={`cursor-pointer text-sm font-medium ${invalid ? "text-destructive" : ""}`}>{group.label}</summary>
                        <div className="mt-4 grid gap-x-5 gap-y-4 sm:grid-cols-2 lg:grid-cols-3">
                            {fields.map(parameter => <TrainingParameterField key={parameter.key} parameter={parameter} context={context} />)}
                        </div>
                    </details>
                );
            })}
            <details className="rounded-md border px-4 py-3" data-training-group="system">
                <summary className="cursor-pointer text-sm font-medium">{trainingParameterGroups.find(group => group.id === "system")!.label}</summary>
                <dl className="mt-3 space-y-3 text-xs">
                    {unavailableTrainingParameters.map(item => (
                        <div key={item.key}>
                            <dt className="font-mono font-medium">{item.key}</dt>
                            <dd className="mt-1 text-muted-foreground">{item.reason}</dd>
                        </div>
                    ))}
                </dl>
            </details>
        </div>
    );
}
