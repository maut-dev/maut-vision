import type { CodedTextDto } from "@/api/coded-text-dto";
import { i18n } from "@/i18n";

/** Keys of coded lines are known only at run time; the typed t accepts literal keys, so lookups by code go through this signature. */
const translate = i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string;

/** The catalog key of a code: "progress.epoch" → "progress:epoch"; null for a code without a namespace. */
function keyOf(code: string): string | null {
    const dot = code.indexOf(".");
    return dot > 0 ? `${code.slice(0, dot)}:${code.slice(dot + 1)}` : null;
}

/** Values for the text: a numeric count becomes a number (plural forms). */
function values(args: Readonly<Record<string, string>> | null): Record<string, unknown> {
    const result: Record<string, unknown> = { ...args };
    if (args?.count !== undefined && /^\d+$/.test(args.count)) result.count = Number(args.count);
    return result;
}

/** Whether the catalogs of the current language have a text (not a group of keys) for the code. */
export function hasCodedText(code: string | null): boolean {
    const key = code ? keyOf(code) : null;
    return key !== null && i18n.exists(key) && typeof i18n.t(key as never, { returnObjects: true } as never) === "string";
}

/**
 * The text of a line the host sent as a code: the catalog text of the current language with the args substituted; the stored text
 * when there is no code or the catalogs do not know it (a line of version 1.0.0, a name, an English message of the worker); the code
 * itself as the last resort. Call during render: a language change renders it again.
 */
export function codedText(value: CodedTextDto): string {
    if (value.code === aiLabelCode && value.args && hasCodedText(value.code)) {
        const inner = innerLabel(value);
        return translate(keyOf(value.code)!, { client: value.args.client ?? "", label: inner ? codedText(inner) : "" });
    }
    if (value.code && hasCodedText(value.code)) return translate(keyOf(value.code)!, values(value.args));
    return value.text ?? value.code ?? "";
}

/** A job label of a job an AI client started through MCP: «ИИ (Claude Code) · <label>»; the label is flattened into its args. */
const aiLabelCode = "jobLabels.ai_label";
const innerArgPrefix = "label.";

/** The label inside jobLabels.ai_label (labelCode, labelText and label.* args); the label itself for any other line. */
export function innerLabel(value: CodedTextDto): CodedTextDto | null {
    if (value.code !== aiLabelCode || !value.args) return value;
    const args: Record<string, string> = {};
    let any = false;
    for (const [name, item] of Object.entries(value.args)) {
        if (!name.startsWith(innerArgPrefix)) continue;
        args[name.slice(innerArgPrefix.length)] = item;
        any = true;
    }
    const code = value.args.labelCode ?? null;
    const text = value.args.labelText ?? null;
    return code === null && text === null ? null : { code, args: any ? args : null, text };
}

/** The code of the worker inside worker.<code>[.<reason>] ("amp_disabled"); null for other codes. */
export function workerCode(value: CodedTextDto): string | null {
    return value.code?.startsWith("worker.") ? value.code.slice("worker.".length).split(".")[0] : null;
}

/**
 * A warning of the worker as the training log and the version passport show it: "code: text" ("amp_disabled: AMP disabled…"); a line of
 * version 1.0.0 already holds "code: text".
 */
export function workerWarningText(value: CodedTextDto): string {
    const code = workerCode(value);
    return code ? translate("trainingLog:warning", { code, message: codedText(value) }) : codedText(value);
}

/** A line of the training log: a warning as «code: text», a system line in the current language, a console line or a stage as is. */
export function logLineText(entry: { kind: string; message: CodedTextDto }): string {
    return entry.kind === "warning" ? workerWarningText(entry.message) : codedText(entry.message);
}
