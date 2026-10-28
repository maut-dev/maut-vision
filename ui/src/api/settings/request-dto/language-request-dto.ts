export type LanguageSetting = "system" | "ru" | "en";

export type GetLanguageRequestDto = Record<string, never>;

/** language — system (as in Windows), ru or en. */
export interface SetLanguageRequestDto {
    language: LanguageSetting;
}