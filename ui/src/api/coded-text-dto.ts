import { isRecord } from "./transport/envelopes";

/**
 * A line the host sends as a code and values (CodedText of Core): code — "<namespace>.<key>" of the UI catalogs (progress.epoch,
 * trainingLog.version_created, jobLabels.version, worker.<code>.<reason>), args — values for its placeholders, text — shown as is when
 * there is no code (a name the user typed, a line stored by version 1.0.0) or the catalogs do not know the code (an English fallback).
 * lib/coded-text.ts turns it into the text of the current language.
 */
export interface CodedTextDto {
    code: string | null;
    args: Readonly<Record<string, string>> | null;
    text: string | null;
}

export function isCodedTextDto(value: unknown): value is CodedTextDto {
    return isRecord(value) && (value.code === null || typeof value.code === "string") && (value.text === null || typeof value.text === "string")
        && (value.args === null || (isRecord(value.args) && Object.values(value.args).every(item => typeof item === "string")));
}
