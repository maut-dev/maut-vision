import type { TrainingParameters, TrainingParameterValue } from "@/api/projects/training/request-dto/training-request-dto";
import {
    isValidTrainingParameter,
    parseListField,
    trainingParameter,
    trainingParameters,
    trainingParametersWithDefaults,
    tunedDefaults,
    type TrainingParameterDefinition,
} from "@/api/projects/training/training-parameters";
import { locale } from "@/lib/format";

/**
 * The form "New training run": input fields (numbers, lists, the manual device) keep the text as the user typed it, checkboxes and
 * choices keep the parameter value. parseTrainingForm turns the form into request parameters by the same rules as Core; wrong keys
 * are highlighted. An empty "Auto" field is null: the worker computes the value (tunedDefaults shows which).
 */
export type TrainingFormValue = string | TrainingParameterValue;
export type TrainingForm = Readonly<Record<string, TrainingFormValue>>;

const textTypes = new Set(["integer", "number", "list"]);

/** The field is typed as text (Input), not chosen. */
export function isTextParameter(parameter: TrainingParameterDefinition): boolean {
    return textTypes.has(parameter.type);
}

function toText(value: TrainingParameterValue): string {
    if (value === null) return "";
    if (Array.isArray(value)) return value.join(", ");
    return String(value);
}

/** A form from parameters (a preset, defaults): missing keys get the tuned defaults. */
export function formFromParameters(parameters: TrainingParameters = {}): TrainingForm {
    const values = trainingParametersWithDefaults(parameters);
    const form: Record<string, TrainingFormValue> = {};
    for (const parameter of trainingParameters) {
        const value = values[parameter.key];
        form[parameter.key] = isTextParameter(parameter) ? toText(value) : value;
    }
    return form;
}

/** A number from a field: a comma as the decimal separator, empty — null; undefined — not a number. */
export function parseNumber(text: string): number | null | undefined {
    const trimmed = text.trim().replace(",", ".");
    if (!trimmed) return null;
    const value = Number(trimmed);
    return Number.isFinite(value) ? value : undefined;
}

function parseField(parameter: TrainingParameterDefinition, input: TrainingFormValue): TrainingParameterValue | undefined {
    if (!isTextParameter(parameter)) return input as TrainingParameterValue;
    const text = typeof input === "string" ? input : toText(input as TrainingParameterValue);
    if (parameter.type === "list") return parseListField(parameter.key as "freeze" | "fraction" | "classes" | "scale", text);
    return parseNumber(text);
}

export interface ParsedTrainingForm {
    parameters: TrainingParameters;
    /** Keys with wrong values in the order of the registry. */
    invalid: string[];
}

export function parseTrainingForm(form: TrainingForm): ParsedTrainingForm {
    const parameters: TrainingParameters = {};
    const invalid: string[] = [];
    for (const parameter of trainingParameters) {
        const value = parseField(parameter, form[parameter.key] ?? null);
        if (value === undefined || !isValidTrainingParameter(parameter.key, value)) invalid.push(parameter.key);
        else parameters[parameter.key] = value;
    }
    return { parameters, invalid };
}

/** The form matches the parameters (of a preset): all registry keys, missing ones — defaults. */
export function sameParameters(form: TrainingParameters, parameters: TrainingParameters): boolean {
    const expected = trainingParametersWithDefaults(parameters);
    return trainingParameters.every(parameter => JSON.stringify(form[parameter.key] ?? null) === JSON.stringify(expected[parameter.key] ?? null));
}

/** "Auto" values the worker computes for the current epochs and batch (wrong fields — defaults). */
export function formTunedDefaults(form: TrainingForm): ReturnType<typeof tunedDefaults> {
    const epochs = parseNumber(String(form.epochs ?? ""));
    const batch = parseNumber(String(form.batch ?? ""));
    return tunedDefaults(typeof epochs === "number" && epochs >= 1 ? epochs : 100, typeof batch === "number" ? batch : 16);
}

/** The switch before a field (rotation): on when the value differs from the off value. */
export function isToggleOn(parameter: TrainingParameterDefinition, form: TrainingForm): boolean {
    if (!parameter.toggle) return true;
    const value = parseNumber(String(form[parameter.key] ?? ""));
    return value !== parameter.toggle.offValue;
}

/** Switching on sets onValue (when the field was off), switching off — offValue. */
export function setToggle(form: TrainingForm, key: string, on: boolean): TrainingForm {
    const parameter = trainingParameter(key);
    if (!parameter?.toggle) return form;
    if (on === isToggleOn(parameter, form)) return form;
    return { ...form, [key]: String(on ? parameter.toggle.onValue : parameter.toggle.offValue) };
}

/** A choice value as a string for Radix Select (null — "Auto"). */
export function choiceKey(value: TrainingParameterValue): string {
    return JSON.stringify(value);
}

export function choiceFromKey(key: string): TrainingParameterValue {
    return JSON.parse(key) as TrainingParameterValue;
}

/** A number in a caption: the decimal separator of the interface language, up to digits digits. */
export function formatParameterNumber(value: number, digits = 6): string {
    return value.toLocaleString(locale(), { maximumFractionDigits: digits, useGrouping: false });
}
