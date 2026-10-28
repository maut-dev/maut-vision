import type { CodedTextDto } from "@/api/coded-text-dto";
import { BridgeError } from "@/api/transport/errors";
import { codedText } from "@/lib/coded-text";
import { i18n } from "@/i18n";

type Details = Readonly<Record<string, string>> | null | undefined;

/** Error keys are codes known only at run time; the typed t accepts literal keys, so lookups by code go through this signature. */
const translate = i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string;

/** Values for the text: a count becomes a number (plural forms), a section key its name in the current language. */
function values(details: Details): Record<string, unknown> {
    const result: Record<string, unknown> = { ...details };
    if (details?.count !== undefined && /^\d+$/.test(details.count)) result.count = Number(details.count);
    if (details?.section !== undefined) {
        const key = `errors:sections.${details.section}`;
        result.section = translate(i18n.exists(key) ? key : "errors:sections.projects");
    }
    return result;
}

/**
 * The text of an error code in the current language with the details substituted («{{field}}»). A code without a text shows the fallback
 * (an old stored message) or the generic text.
 */
export function errorText(code: string, details?: Details, fallback?: string | null): string {
    const key = `errors:${code}`;
    const options = values(details);
    if (!i18n.exists(key, options)) return fallback || translate("errors:generic");
    return translate(key, options);
}

/** The text of a caught error: a bridge error by its code, an error of the page by its message, otherwise the fallback or the generic text. */
export function errorMessage(error: unknown, fallback?: string): string {
    if (error instanceof BridgeError) return errorText(error.code, error.details);
    if (error instanceof Error && error.message) return error.message;
    return fallback ?? translate("errors:generic");
}

/** A stored job error: the text of its code; the stored message only for a code the catalogs do not know (an older version). */
export function jobErrorText(error: { code: string; message: string | null }): string {
    return errorText(error.code, null, error.message);
}

/**
 * A training run or check error: for a failure the worker reported itself, the text of its worker code with values (its English message
 * for a code the catalogs do not know, its stored text for a run of 1.0.0); otherwise the text of the error code.
 */
export function workerErrorText(error: { code: string; workerMessage: CodedTextDto | null }): string {
    return (error.workerMessage && codedText(error.workerMessage)) || errorText(error.code);
}

/** Dialogs that list deletion blockers; the same code reads differently in each. */
export type BlockerArea = "catalog" | "images" | "backgrounds" | "datasets" | "training";

export interface Blocker {
    code: string;
    count: number;
    examples: readonly string[];
}

/** A line of the blocker list: «Используется в пресетах генерации: Пресет «A» и другие.», «Набор используется: 2.»… */
export function blockerText(area: BlockerArea, blocker: Blocker): string {
    const { code, count, examples } = blocker;
    if (area === "training") return errorText(code);
    const end = translate(count > examples.length ? "errors:blocker.listMore" : "errors:blocker.listEnd");
    const options: Record<string, unknown> = { count, list: examples.join(", "), end, name: examples[0] ?? "" };
    if (code === "generation.preset_reference") options.list = examples.map(name => translate("errors:blocker.presetName", { name })).join(", ");
    const key = `errors:blocker.${area}.${code}`;
    if (i18n.exists(key)) return translate(key, options);
    const withExamples = `errors:blocker.${area}.defaultExamples`;
    return translate(examples.length && i18n.exists(withExamples) ? withExamples : `errors:blocker.${area}.default`, options);
}

/** A warning of the dataset plan with its count. */
export function planWarningText(warning: { code: string; count: number }): string {
    const key = `errors:planWarning.${warning.code}`;
    return translate(i18n.exists(key) ? key : "errors:planWarning.default", { count: warning.count });
}

/**
 * Why a background source was skipped: with the sizes «Размер a×b, нужен W×H» and «Рамка W×H не помещается в кадр a×b», otherwise the
 * text of the code (video.frame_unavailable among them).
 */
export function backgroundReasonText(reason: string, width: number | null, height: number | null, target: { width: number; height: number }): string {
    const key = `errors:backgroundReason.${reason}`;
    if (width !== null && height !== null && i18n.exists(key)) {
        return translate(key, { actual: `${width}×${height}`, needed: `${target.width}×${target.height}` });
    }
    return errorText(reason);
}
