import { useEffect, useId, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
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
 * The device of a check: CPU and the GPUs of listGpus as in training, without the automatic choice; without a driver, manual entry. While
 * no device is chosen, the first GPU is set.
 */
export function DeviceSelect({ value, onChange, gpus, disabled, invalid, testId }: {
    value: string;
    onChange: (value: string) => void;
    gpus: GpuInventoryResponseDto | null | undefined;
    disabled?: boolean;
    invalid?: boolean;
    testId: string;
}) {
    const { t } = useTranslation("checks");
    const { options, manualEntry } = checkDeviceOptions(gpus ?? null);
    const listed = options.some(option => option.value === value);

    useEffect(() => {
        if (!value && gpus !== undefined) onChange(defaultDevice(gpus));
    }, [value, gpus, onChange]);

    return (
        <div className="space-y-2">
            <Select value={value} disabled={disabled} onValueChange={next => { if (next) onChange(next); }}>
                <SelectTrigger className="w-full" aria-label={t("fields.device")} aria-invalid={invalid || undefined} data-testid={testId}>
                    <SelectValue placeholder={gpus === undefined ? t("fields.findingGpus") : t("fields.chooseDevice")} />
                </SelectTrigger>
                <SelectContent>
                    {options.map(option => <SelectItem key={option.value} value={option.value}>{option.label}</SelectItem>)}
                    {value && !listed && <SelectItem value={value}>{t("fields.manual", { value })}</SelectItem>}
                </SelectContent>
            </Select>
            {manualEntry && gpus !== undefined && (
                <Input aria-label={t("fields.manualAria")} placeholder={t("fields.manualPlaceholder")} disabled={disabled} value={listed ? "" : value}
                    onChange={event => onChange(event.target.value.trim())} />
            )}
        </div>
    );
}

/** Fields shared by the check modes: the confidence threshold, detections per frame, the device. */
export function CheckParameterFields({ value, onChange, gpus, disabled, invalid, testPrefix }: {
    value: CheckParameterValues;
    onChange: (value: CheckParameterValues) => void;
    gpus: GpuInventoryResponseDto | null | undefined;
    disabled?: boolean;
    invalid?: CheckParameterField;
    testPrefix: string;
}) {
    const { t } = useTranslation("checks");
    const id = useId();
    return (
        <>
            <Field label={t("fields.confidence")} htmlFor={`${id}-confidence`}>
                <Input id={`${id}-confidence`} type="number" min={0} max={1} step={0.05} disabled={disabled} aria-invalid={invalid === "confidence" || undefined}
                    data-testid={`${testPrefix}-confidence`} value={value.confidence} onChange={event => onChange({ ...value, confidence: event.target.value })} />
            </Field>
            <Field label={t("fields.maxDetections")} htmlFor={`${id}-limit`}>
                <Input id={`${id}-limit`} type="number" min={1} step={1} placeholder={t("fields.auto")} disabled={disabled} aria-invalid={invalid === "maxDetections" || undefined}
                    data-testid={`${testPrefix}-max-detections`} value={value.maxDetections} onChange={event => onChange({ ...value, maxDetections: event.target.value })} />
            </Field>
            <Field label={t("fields.device")}>
                <DeviceSelect value={value.device} gpus={gpus} disabled={disabled} invalid={invalid === "device"} testId={`${testPrefix}-device`}
                    onChange={device => onChange({ ...value, device })} />
            </Field>
        </>
    );
}
