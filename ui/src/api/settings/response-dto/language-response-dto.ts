import { isRecord } from "../../transport/envelopes";
import type { LanguageSetting } from "../request-dto/language-request-dto";

export type Language = "ru" | "en";

/** setting — what the user chose; language — what it resolves to. Also the event settings.languageChanged. */
export interface LanguageResponseDto {
    setting: LanguageSetting;
    language: Language;
}

export function isLanguageResponseDto(value: unknown): value is LanguageResponseDto {
    return isRecord(value) && (value.setting === "system" || value.setting === "ru" || value.setting === "en")
        && (value.language === "ru" || value.language === "en");
}