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

/**
 * Форма «Новый запуск обучения»: поля ввода (числа, списки, устройство вручную) хранят текст как его набрал пользователь,
 * флажки и выборы — значение параметра. parseTrainingForm превращает форму в параметры запроса теми же правилами, что у Core;
 * неверные ключи подсвечиваются. Пустое поле с «Авто» — null: значение посчитает worker (tunedDefaults показывает какое).
 */
export type TrainingFormValue = string | TrainingParameterValue;
export type TrainingForm = Readonly<Record<string, TrainingFormValue>>;

const textTypes = new Set(["integer", "number", "list"]);

/** Поле вводится текстом (Input), а не выбирается. */
export function isTextParameter(parameter: TrainingParameterDefinition): boolean {
    return textTypes.has(parameter.type);
}

function toText(value: TrainingParameterValue): string {
    if (value === null) return "";
    if (Array.isArray(value)) return value.join(", ");
    return String(value);
}

/** Форма из параметров (пресет, значения по умолчанию): недостающие ключи — подобранные значения по умолчанию. */
export function formFromParameters(parameters: TrainingParameters = {}): TrainingForm {
    const values = trainingParametersWithDefaults(parameters);
    const form: Record<string, TrainingFormValue> = {};
    for (const parameter of trainingParameters) {
        const value = values[parameter.key];
        form[parameter.key] = isTextParameter(parameter) ? toText(value) : value;
    }
    return form;
}

/** Число из поля: запятая как десятичный разделитель, пусто — null; undefined — не число. */
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
    /** Ключи с неверными значениями в порядке реестра. */
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

/** Форма совпадает с параметрами (пресета): все ключи реестра, недостающие — значения по умолчанию. */
export function sameParameters(form: TrainingParameters, parameters: TrainingParameters): boolean {
    const expected = trainingParametersWithDefaults(parameters);
    return trainingParameters.every(parameter => JSON.stringify(form[parameter.key] ?? null) === JSON.stringify(expected[parameter.key] ?? null));
}

/** Подобранные worker значения «Авто» полей для текущих эпох и batch (неверные поля — по умолчанию). */
export function formTunedDefaults(form: TrainingForm): ReturnType<typeof tunedDefaults> {
    const epochs = parseNumber(String(form.epochs ?? ""));
    const batch = parseNumber(String(form.batch ?? ""));
    return tunedDefaults(typeof epochs === "number" && epochs >= 1 ? epochs : 100, typeof batch === "number" ? batch : 16);
}

/** Переключатель перед полем (повороты): включён, если значение отличается от выключенного. */
export function isToggleOn(parameter: TrainingParameterDefinition, form: TrainingForm): boolean {
    if (!parameter.toggle) return true;
    const value = parseNumber(String(form[parameter.key] ?? ""));
    return value !== parameter.toggle.offValue;
}

/** Включение ставит onValue (если поле было выключено), выключение — offValue. */
export function setToggle(form: TrainingForm, key: string, on: boolean): TrainingForm {
    const parameter = trainingParameter(key);
    if (!parameter?.toggle) return form;
    if (on === isToggleOn(parameter, form)) return form;
    return { ...form, [key]: String(on ? parameter.toggle.onValue : parameter.toggle.offValue) };
}

/** Значение выбора в строку для Radix Select (null — «Авто»). */
export function choiceKey(value: TrainingParameterValue): string {
    return JSON.stringify(value);
}

export function choiceFromKey(key: string): TrainingParameterValue {
    return JSON.parse(key) as TrainingParameterValue;
}

/** Число в подписи: русская запятая, до digits знаков. */
export function formatParameterNumber(value: number, digits = 6): string {
    return value.toLocaleString("ru-RU", { maximumFractionDigits: digits, useGrouping: false });
}
