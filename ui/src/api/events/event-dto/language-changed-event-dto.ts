import type { LanguageResponseDto } from "../../settings/response-dto/language-response-dto";

export interface LanguageChangedEventDto {
    kind: "event";
    event: "settings.languageChanged";
    data: LanguageResponseDto;
}