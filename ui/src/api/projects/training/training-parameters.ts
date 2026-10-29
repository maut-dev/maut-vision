import { trainingText } from "@/lib/training/locale";
import type { GpuInventoryResponseDto } from "./response-dto/training-runtime-response-dto";
import type { TrainingParameters, TrainingParameterValue } from "./request-dto/training-request-dto";
import type { TrainingRunState } from "./response-dto/training-run-response-dto";

/**
 * The registry of training parameters for the form: worker protocol keys (camelCase), labels and hints (the training catalog,
 * parameters.<key>), groups, types and ranges, defaults tuned in the worker (python/worker/protocol/request.py). Range checks match Core
 * (TrainingParameterRules): a wrong value is refused with the code training.parameter_invalid and details.field = the key.
 *
 * lr0, warmupEpochs, patience and workers default to null — the worker computes them; the form shows the actual value through
 * tunedDefaults(). With optimizer "auto" the lr0 field is marked "Chosen by the optimizer" (isLr0ChosenByOptimizer).
 *
 * Labels, hints and reasons are getters: they are read at render time, so a language change relabels the form.
 */

export type TrainingParameterGroup =
    | "training" | "optimizer" | "loss" | "data" | "reproducibility" | "augmentation" | "validation" | "checkpoints";

const groupIds = ["training", "optimizer", "loss", "data", "reproducibility", "augmentation", "validation", "checkpoints", "system"] as const;

/** Groups of "Advanced" in display order; system — "Managed by the application and unavailable settings". */
export const trainingParameterGroups: readonly { id: TrainingParameterGroup | "system"; label: string }[] = groupIds.map(id => ({
    id,
    get label() { return trainingText(`parameterGroups.${id}`); },
}));

/**
 * Field kind: integer / number — a number (min, max; exclusiveMin — strictly greater than min); boolean — a checkbox; select — one of
 * choices; device — "Device" (deviceOptions); list — numbers or names separated by commas (parseListField).
 */
export type TrainingParameterType = "integer" | "number" | "boolean" | "select" | "device" | "list";

export interface TrainingParameterChoice {
    value: TrainingParameterValue;
    label: string;
}

export interface TrainingParameterDefinition {
    key: string;
    /** The Ultralytics parameter name — a caption in small print. */
    ultralyticsKey: string;
    label: string;
    hint?: string;
    group: TrainingParameterGroup;
    type: TrainingParameterType;
    /** The tuned default; null — "Auto": the worker computes the value (see tunedDefaults). */
    defaultValue: TrainingParameterValue;
    /** null is allowed: an empty field is "Auto". */
    nullable?: boolean;
    min?: number;
    max?: number;
    exclusiveMin?: boolean;
    choices?: readonly TrainingParameterChoice[];
    /** A main field of the form (visible without "Advanced"). */
    base?: boolean;
    /** A switch before the field: off — the value offValue, on — the field is active (onValue — the value when switched on). */
    toggle?: { label: string; offValue: number; onValue: number };
}

/** A definition as written below: hint: true — the catalog has parameters.<key>.hint; toggle: true — parameters.<key>.toggle. */
type ParameterSpec = Omit<TrainingParameterDefinition, "label" | "hint" | "toggle"> & {
    hint?: true;
    toggle?: { offValue: number; onValue: number };
};

const on: TrainingParameterChoice = { value: true, get label() { return trainingText("parameterChoices.on"); } };
const off: TrainingParameterChoice = { value: false, get label() { return trainingText("parameterChoices.off"); } };
const plain = (...values: string[]): TrainingParameterChoice[] => values.map(value => ({ value, label: value }));

const specs: readonly ParameterSpec[] = [
    // Duration and mode of training.
    { key: "epochs", ultralyticsKey: "epochs", group: "training", type: "integer", defaultValue: 100, min: 1, max: 100_000, base: true, hint: true },
    { key: "batch", ultralyticsKey: "batch", group: "training", type: "number", defaultValue: 16, base: true, hint: true },
    { key: "device", ultralyticsKey: "device", group: "training", type: "device", defaultValue: "0", base: true, hint: true },
    { key: "amp", ultralyticsKey: "amp", group: "training", type: "select", defaultValue: true, base: true,
        choices: [on, off, ...plain("fp16", "bf16", "fp32")], hint: true },
    { key: "time", ultralyticsKey: "time", group: "training", type: "number", defaultValue: null, nullable: true, min: 0, exclusiveMin: true, hint: true },
    { key: "patience", ultralyticsKey: "patience", group: "training", type: "integer", defaultValue: null, nullable: true, min: 0, hint: true },
    { key: "pretrained", ultralyticsKey: "pretrained", group: "training", type: "boolean", defaultValue: true, hint: true },
    { key: "clsRemap", ultralyticsKey: "cls_remap", group: "training", type: "boolean", defaultValue: true },
    { key: "freeze", ultralyticsKey: "freeze", group: "training", type: "list", defaultValue: null, nullable: true, hint: true },
    { key: "channelsLast", ultralyticsKey: "channels_last", group: "training", type: "select", defaultValue: null, nullable: true, choices: [on, off], hint: true },
    { key: "verbose", ultralyticsKey: "verbose", group: "training", type: "boolean", defaultValue: true },
    // Optimizer and learning rate.
    { key: "optimizer", ultralyticsKey: "optimizer", group: "optimizer", type: "select", defaultValue: "AdamW",
        choices: plain("auto", "SGD", "MuSGD", "Adam", "Adamax", "AdamW", "NAdam", "RAdam", "RMSProp"), hint: true },
    { key: "lr0", ultralyticsKey: "lr0", group: "optimizer", type: "number", defaultValue: null, nullable: true, min: 0, exclusiveMin: true, base: true, hint: true },
    { key: "lrf", ultralyticsKey: "lrf", group: "optimizer", type: "number", defaultValue: 0.01, min: 0, max: 1 },
    { key: "momentum", ultralyticsKey: "momentum", group: "optimizer", type: "number", defaultValue: 0.9, min: 0, max: 1 },
    { key: "weightDecay", ultralyticsKey: "weight_decay", group: "optimizer", type: "number", defaultValue: 0.0005, min: 0 },
    { key: "warmupEpochs", ultralyticsKey: "warmup_epochs", group: "optimizer", type: "number", defaultValue: null, nullable: true, min: 0, base: true, hint: true },
    { key: "warmupMomentum", ultralyticsKey: "warmup_momentum", group: "optimizer", type: "number", defaultValue: 0.8, min: 0, max: 1 },
    { key: "warmupBiasLr", ultralyticsKey: "warmup_bias_lr", group: "optimizer", type: "number", defaultValue: 0, min: 0 },
    { key: "cosLr", ultralyticsKey: "cos_lr", group: "optimizer", type: "boolean", defaultValue: false },
    // Loss function.
    { key: "box", ultralyticsKey: "box", group: "loss", type: "number", defaultValue: 7.5, min: 0 },
    { key: "cls", ultralyticsKey: "cls", group: "loss", type: "number", defaultValue: 1, min: 0 },
    { key: "clsPw", ultralyticsKey: "cls_pw", group: "loss", type: "number", defaultValue: 0, min: 0 },
    { key: "dfl", ultralyticsKey: "dfl", group: "loss", type: "number", defaultValue: 1.5, min: 0 },
    { key: "nbs", ultralyticsKey: "nbs", group: "loss", type: "integer", defaultValue: 64, min: 1 },
    // Data and loading.
    { key: "cache", ultralyticsKey: "cache", group: "data", type: "select", defaultValue: false, choices: [off, on, ...plain("ram", "disk")] },
    { key: "workers", ultralyticsKey: "workers", group: "data", type: "integer", defaultValue: null, nullable: true, min: 0, max: 256, hint: true },
    { key: "singleCls", ultralyticsKey: "single_cls", group: "data", type: "boolean", defaultValue: false, hint: true },
    { key: "rect", ultralyticsKey: "rect", group: "data", type: "boolean", defaultValue: false },
    { key: "fraction", ultralyticsKey: "fraction", group: "data", type: "list", defaultValue: 1, hint: true },
    { key: "classes", ultralyticsKey: "classes", group: "data", type: "list", defaultValue: null, nullable: true, hint: true },
    // Reproducibility.
    { key: "seed", ultralyticsKey: "seed", group: "reproducibility", type: "integer", defaultValue: 0, min: 0, max: 2_147_483_647, base: true },
    { key: "deterministic", ultralyticsKey: "deterministic", group: "reproducibility", type: "boolean", defaultValue: true },
    // Augmentations.
    { key: "hsvH", ultralyticsKey: "hsv_h", group: "augmentation", type: "number", defaultValue: 0.01, min: 0, max: 1 },
    { key: "hsvS", ultralyticsKey: "hsv_s", group: "augmentation", type: "number", defaultValue: 0.4, min: 0, max: 1 },
    { key: "hsvV", ultralyticsKey: "hsv_v", group: "augmentation", type: "number", defaultValue: 0.5, min: 0, max: 1 },
    { key: "degrees", ultralyticsKey: "degrees", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 180,
        toggle: { offValue: 0, onValue: 10 }, hint: true },
    { key: "translate", ultralyticsKey: "translate", group: "augmentation", type: "number", defaultValue: 0.05, min: 0, max: 1, hint: true },
    { key: "scale", ultralyticsKey: "scale", group: "augmentation", type: "list", defaultValue: 0.1, hint: true },
    { key: "shear", ultralyticsKey: "shear", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 180 },
    { key: "perspective", ultralyticsKey: "perspective", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "flipud", ultralyticsKey: "flipud", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "fliplr", ultralyticsKey: "fliplr", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "bgr", ultralyticsKey: "bgr", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "mosaic", ultralyticsKey: "mosaic", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "closeMosaic", ultralyticsKey: "close_mosaic", group: "augmentation", type: "integer", defaultValue: 10, min: 0 },
    { key: "mixup", ultralyticsKey: "mixup", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "cutmix", ultralyticsKey: "cutmix", group: "augmentation", type: "number", defaultValue: 0, min: 0, max: 1 },
    { key: "multiScale", ultralyticsKey: "multi_scale", group: "augmentation", type: "number", defaultValue: 0, min: 0, hint: true },
    // Quality check.
    { key: "val", ultralyticsKey: "val", group: "validation", type: "boolean", defaultValue: true, hint: true },
    { key: "split", ultralyticsKey: "split", group: "validation", type: "select", defaultValue: "val", choices: plain("val", "test", "train"), hint: true },
    { key: "conf", ultralyticsKey: "conf", group: "validation", type: "number", defaultValue: null, nullable: true, min: 0, max: 1, hint: true },
    { key: "iou", ultralyticsKey: "iou", group: "validation", type: "number", defaultValue: 0.7, min: 0, max: 1 },
    { key: "maxDet", ultralyticsKey: "max_det", group: "validation", type: "integer", defaultValue: 300, min: 1 },
    { key: "saveJson", ultralyticsKey: "save_json", group: "validation", type: "boolean", defaultValue: false },
    // Saving and reports.
    { key: "save", ultralyticsKey: "save", group: "checkpoints", type: "boolean", defaultValue: true, hint: true },
    { key: "savePeriod", ultralyticsKey: "save_period", group: "checkpoints", type: "integer", defaultValue: -1, min: -1, hint: true },
    { key: "plots", ultralyticsKey: "plots", group: "checkpoints", type: "boolean", defaultValue: true },
];

function withTexts({ hint, toggle, ...spec }: ParameterSpec): TrainingParameterDefinition {
    const definition = { ...spec } as TrainingParameterDefinition;
    Object.defineProperty(definition, "label", { enumerable: true, get: () => trainingText(`parameters.${spec.key}.label`) });
    if (hint) Object.defineProperty(definition, "hint", { enumerable: true, get: () => trainingText(`parameters.${spec.key}.hint`) });
    if (toggle) definition.toggle = { ...toggle, get label() { return trainingText(`parameters.${spec.key}.toggle`); } };
    return definition;
}

export const trainingParameters: readonly TrainingParameterDefinition[] = specs.map(withTexts);

export const trainingParameterKeys: readonly string[] = trainingParameters.map(parameter => parameter.key);

const byKey = new Map(trainingParameters.map(parameter => [parameter.key, parameter]));

export function trainingParameter(key: string): TrainingParameterDefinition | undefined {
    return byKey.get(key);
}

/** Keys unavailable in the form: system — the application manages them, unsupported — absent from the pinned build, not_applicable — not for detect train. */
export interface UnavailableTrainingParameter {
    key: string;
    status: "system" | "unsupported" | "not_applicable";
    reason: string;
}

/** reason — a key of unavailableReasons in the training catalog. */
const unavailable = (status: UnavailableTrainingParameter["status"], reason: string) => (key: string): UnavailableTrainingParameter => ({
    key,
    status,
    get reason() { return trainingText(`unavailableReasons.${reason}`); },
});

export const unavailableTrainingParameters: readonly UnavailableTrainingParameter[] = [
    ...["task", "mode", "model", "data", "imgsz", "project", "name", "exist_ok", "cfg"].map(unavailable("system", "system")),
    ...["profile", "compile", "resume", "quantize", "distill_model", "dis"].map(key => unavailable("unsupported", key)(key)),
    ...["overlap_mask", "mask_ratio", "dropout", "pose", "kobj", "rle", "angle", "dlog", "dgrad", "dlam", "copy_paste", "copy_paste_mode", "auto_augment", "erasing"]
        .map(unavailable("not_applicable", "notDetect")),
    ...["dnn", "nms", "source", "vid_stride", "stream_buffer", "visualize", "augment", "agnostic_nms", "retina_masks", "embed", "show", "save_frames", "save_txt",
        "save_conf", "save_crop", "show_labels", "show_conf", "show_boxes", "line_width", "format", "keras", "optimize", "dynamic", "simplify", "opset", "workspace",
        "tracker"].map(unavailable("not_applicable", "otherMode")),
];

/** All supported parameters with their defaults, with the given values (a preset, the form) on top. */
export function trainingParametersWithDefaults(values: TrainingParameters = {}): TrainingParameters {
    const result: TrainingParameters = {};
    for (const parameter of trainingParameters) result[parameter.key] = Object.hasOwn(values, parameter.key) ? values[parameter.key] : parameter.defaultValue;
    return result;
}

/**
 * Values the worker computes for "Auto" fields: lr0 = 0.001 · batch / 32 (for AutoBatch and fractions — by batch 16), warmup
 * max(1, min(5, epochs / 10)) · batch / 32, at least one epoch and at most a quarter of training, patience = max(10, min(40, epochs)), workers 0.
 */
export function tunedDefaults(epochs: number, batch: number): { lr0: number; warmupEpochs: number; patience: number; workers: number } {
    const reference = batch >= 1 ? Math.floor(batch) : 16;
    const safeEpochs = Math.max(1, Math.floor(epochs));
    const base = Math.max(1, Math.min(5, safeEpochs / 10));
    const warmup = Math.min(base * Math.max(1, reference) / 32, Math.max(1, safeEpochs * 0.25));
    return {
        lr0: Math.round(0.001 * Math.max(1, reference) / 32 * 1e6) / 1e6,
        warmupEpochs: Math.round(warmup * 100) / 100,
        patience: Math.max(10, Math.min(40, safeEpochs)),
        workers: 0,
    };
}

/** With optimizer "auto" the optimizer chooses lr0: the field is marked "Chosen by the optimizer". */
export function isLr0ChosenByOptimizer(values: TrainingParameters): boolean {
    return (values.optimizer ?? "AdamW") === "auto";
}

const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);
const integer = (value: unknown, min: number, max = Number.MAX_SAFE_INTEGER) => finite(value) && Number.isInteger(value) && value >= min && value <= max;

/** The same check as in Core; false — the field is highlighted ("Check the parameter value"). */
export function isValidTrainingParameter(key: string, value: unknown): boolean {
    const parameter = byKey.get(key);
    if (!parameter) return false;
    if (value === null) return parameter.nullable === true;
    switch (key) {
        case "batch":
            return finite(value) && (value === -1 || (value > 0 && value < 1) || (Number.isInteger(value) && value >= 1 && value <= 100_000));
        case "device":
            return typeof value === "string" && /^(cpu|-1|[0-9]{1,3})$/.test(value);
        case "amp":
            return typeof value === "boolean" || value === "fp16" || value === "bf16" || value === "fp32";
        case "cache":
            return typeof value === "boolean" || value === "ram" || value === "disk";
        case "channelsLast":
            return typeof value === "boolean";
        case "fraction": {
            const items = Array.isArray(value) ? value : [value];
            return items.length >= 1 && items.length <= 3 && items.every((item, index) => finite(item)
                && ((item === 0 && index === 2) || (item > 0 && item <= 1) || (item > 1 && Number.isInteger(item))));
        }
        case "freeze":
            return integer(value, 0, 10_000) || (Array.isArray(value) && value.every(item => integer(item, 0, 10_000)
                || (typeof item === "string" && /^[0-9]+(\.[A-Za-z0-9_]+)*$/.test(item))));
        case "classes":
            return Array.isArray(value) && value.length > 0 && value.every(item => integer(item, 0, 100_000)) && new Set(value).size === value.length;
        case "scale":
            return (finite(value) && value >= 0)
                || (Array.isArray(value) && value.length === 2 && value.every(item => finite(item) && item > 0) && (value[0] as number) <= (value[1] as number));
    }
    switch (parameter.type) {
        case "boolean":
            return typeof value === "boolean";
        case "select":
            return (parameter.choices ?? []).some(choice => choice.value === value);
        case "integer":
            return integer(value, parameter.min ?? Number.MIN_SAFE_INTEGER, parameter.max);
        case "number":
            return finite(value) && (parameter.exclusiveMin ? value > (parameter.min ?? -Infinity) : value >= (parameter.min ?? -Infinity))
                && value <= (parameter.max ?? Infinity);
        default:
            return false;
    }
}

/** Keys with wrong values (empty — the form can be sent). */
export function invalidTrainingParameters(values: TrainingParameters): string[] {
    return Object.entries(values).filter(([key, value]) => !isValidTrainingParameter(key, value)).map(([key]) => key);
}

/**
 * A list field from text: "5" → 5, "0, 2" → [0, 2], for freeze also module names "23.cv2"; empty text — null.
 * undefined — the text does not parse (the field is highlighted).
 */
export function parseListField(key: "freeze" | "fraction" | "classes" | "scale", text: string): TrainingParameterValue | undefined {
    const trimmed = text.trim();
    if (!trimmed) return key === "freeze" || key === "classes" ? null : undefined;
    const parts = trimmed.split(",").map(part => part.trim());
    if (parts.some(part => !part)) return undefined;
    const items = parts.map(part => (Number.isFinite(Number(part)) ? Number(part) : part));
    if (key !== "freeze" && items.some(item => typeof item !== "number")) return undefined;
    const value = items.length === 1 && key !== "classes" ? items[0] : items;
    return isValidTrainingParameter(key, value) ? value as TrainingParameterValue : undefined;
}

export interface TrainingDeviceOption {
    value: string;
    label: string;
}

/** "Device": the processor, "GPU N — name, VRAM", "Any free GPU"; without a driver — only the processor and manual entry. */
export function deviceOptions(inventory: GpuInventoryResponseDto | null): { options: TrainingDeviceOption[]; manualEntry: boolean } {
    const options: TrainingDeviceOption[] = [{ value: "cpu", get label() { return trainingText("devices.cpu"); } }];
    if (!inventory?.driverAvailable || inventory.gpus.length === 0) return { options, manualEntry: true };
    for (const gpu of inventory.gpus) {
        const memory = (gpu.memoryTotalMb / 1024).toFixed(gpu.memoryTotalMb % 1024 === 0 ? 0 : 1);
        options.push({ value: String(gpu.index), get label() { return trainingText("devices.gpu", { index: gpu.index, name: gpu.name, memory }); } });
    }
    options.push({ value: "-1", get label() { return trainingText("devices.anyGpu"); } });
    return { options, manualEntry: false };
}

/** The default device: the first GPU, without GPUs — the processor. */
export function defaultDevice(inventory: GpuInventoryResponseDto | null): string {
    return inventory?.driverAvailable && inventory.gpus.length > 0 ? String(inventory.gpus[0].index) : "cpu";
}

/** A dataset for the form warnings: state, match with the project input, frames by part. */
export interface TrainingFormDataset {
    integrityState: "unverified" | "verified" | "corrupt";
    inputSizeMatches: boolean;
    counts: { train: number; val: number; test: number };
}

export interface TrainingFormWarning {
    code: "dataset_corrupt" | "input_size_mismatch" | "batch_exceeds_frames" | "no_validation";
    message: string;
    /** blocking — "Start training" is unavailable. */
    blocking: boolean;
}

/** Warnings of the form; message is read at render time. */
export function trainingFormWarnings(dataset: TrainingFormDataset, values: TrainingParameters): TrainingFormWarning[] {
    const warnings: TrainingFormWarning[] = [];
    const warning = (code: TrainingFormWarning["code"], blocking: boolean, args?: Record<string, unknown>): TrainingFormWarning =>
        ({ code, blocking, get message() { return trainingText(`formWarnings.${code}`, args); } });
    if (dataset.integrityState === "corrupt") warnings.push(warning("dataset_corrupt", true));
    if (!dataset.inputSizeMatches) warnings.push(warning("input_size_mismatch", true));
    const batch = values.batch;
    if (typeof batch === "number" && batch >= 1 && batch > dataset.counts.train) {
        warnings.push(warning("batch_exceeds_frames", false, { batch, frames: dataset.counts.train }));
    }
    if (dataset.counts.val === 0) warnings.push(warning("no_validation", false));
    return warnings;
}

const runStates: readonly TrainingRunState[] = ["queued", "preparing", "training", "cancelling", "succeeded", "failed", "cancelled", "interrupted"];

/** Labels of run states — the same on every screen (read at render time). */
export const trainingRunStateLabels: Readonly<Record<TrainingRunState, string>> = Object.defineProperties({} as Record<TrainingRunState, string>,
    Object.fromEntries(runStates.map(state => [state, { enumerable: true, get: () => trainingText(`runStates.${state}`) }])));

/** An active run: "Cancel training" is available. */
export function isActiveTrainingRun(state: TrainingRunState): boolean {
    return state === "queued" || state === "preparing" || state === "training" || state === "cancelling";
}

/** Labels of worker stages for "Current run". */
export const trainingStageLabels: Readonly<Record<string, string>> = Object.defineProperties({} as Record<string, string>,
    Object.fromEntries(["validating", "loading_model", "training", "evaluating", "finalizing", "exporting"]
        .map(stage => [stage, { enumerable: true, get: () => trainingText(`stages.${stage}`) }])));

/** Labels of epoch metrics and losses (the charts "Quality by epoch" and "Losses by epoch"). */
export const trainingMetricLabels: Readonly<Record<string, string>> = Object.defineProperties({} as Record<string, string>,
    Object.fromEntries(["precision", "recall", "mAP50", "mAP50_95", "box_loss", "cls_loss", "dfl_loss", "l1_loss", "valBoxLoss", "valClsLoss", "valDflLoss"]
        .map(metric => [metric, { enumerable: true, get: () => trainingText(`metrics.${metric}`) }])));
