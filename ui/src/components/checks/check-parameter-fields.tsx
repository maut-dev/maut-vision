import { useEffect, useId, type ReactNode } from "react";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { defaultDevice } from "@/api/projects/training/training-parameters";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { checkDeviceOptions, type CheckParameterField, type CheckParameterValues } from "@/lib/checks/check-view";

export function Field({ label, htmlFor, children, hint }: { label: string; htmlFor?: string; children: ReactNode; hint?: ReactNode }) {
    return (
        <div className="min-w-0 space-y-1">
            <label className="block text-xs text-muted-foreground" htmlFor={htmlFor}>{label}</label>
            {children}
            {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
        </div>
    );
}

/**
 * «Устройство» проверки: «Процессор» и видеокарты из listGpus, как в обучении, без автовыбора; без драйвера — ручной ввод. Пока
 * устройство не выбрано, ставится первая видеокарта.
 */
export function DeviceSelect({ value, onChange, gpus, disabled, invalid, testId }: {
    value: string;
    onChange: (value: string) => void;
    gpus: GpuInventoryResponseDto | null | undefined;
    disabled?: boolean;
    invalid?: boolean;
    testId: string;
}) {
    const { options, manualEntry } = checkDeviceOptions(gpus ?? null);
    const listed = options.some(option => option.value === value);

    useEffect(() => {
        if (!value && gpus !== undefined) onChange(defaultDevice(gpus));
    }, [value, gpus, onChange]);

    return (
        <div className="space-y-2">
            <Select value={value} disabled={disabled} onValueChange={next => { if (next) onChange(next); }}>
                <SelectTrigger className="w-full" aria-label="Устройство" aria-invalid={invalid || undefined} data-testid={testId}>
                    <SelectValue placeholder={gpus === undefined ? "Поиск видеокарт…" : "Выберите устройство"} />
                </SelectTrigger>
                <SelectContent>
                    {options.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                    {value && !listed && <SelectItem value={value}>Вручную: {value}</SelectItem>}
                </SelectContent>
            </Select>
            {manualEntry && gpus !== undefined && (
                <Input aria-label="Устройство вручную" placeholder="cpu или номер видеокарты" disabled={disabled} value={listed ? "" : value}
                    onChange={event => onChange(event.target.value.trim())} />
            )}
        </div>
    );
}

/** Общие поля режимов «Проверок»: «Порог уверенности (0–1)», «Обнаружения в кадре…», «Устройство». */
export function CheckParameterFields({ value, onChange, gpus, disabled, invalid, testPrefix }: {
    value: CheckParameterValues;
    onChange: (value: CheckParameterValues) => void;
    gpus: GpuInventoryResponseDto | null | undefined;
    disabled?: boolean;
    invalid?: CheckParameterField;
    testPrefix: string;
}) {
    const id = useId();
    return (
        <>
            <Field label="Порог уверенности (0–1)" htmlFor={`${id}-confidence`}>
                <Input id={`${id}-confidence`} type="number" min={0} max={1} step={0.05} disabled={disabled} aria-invalid={invalid === "confidence" || undefined}
                    data-testid={`${testPrefix}-confidence`} value={value.confidence} onChange={event => onChange({ ...value, confidence: event.target.value })} />
            </Field>
            <Field label="Обнаружения в кадре (пусто — все кандидаты модели)" htmlFor={`${id}-limit`}>
                <Input id={`${id}-limit`} type="number" min={1} step={1} placeholder="Автоматически" disabled={disabled} aria-invalid={invalid === "maxDetections" || undefined}
                    data-testid={`${testPrefix}-max-detections`} value={value.maxDetections} onChange={event => onChange({ ...value, maxDetections: event.target.value })} />
            </Field>
            <Field label="Устройство">
                <DeviceSelect value={value.device} gpus={gpus} disabled={disabled} invalid={invalid === "device"} testId={`${testPrefix}-device`}
                    onChange={device => onChange({ ...value, device })} />
            </Field>
        </>
    );
}
