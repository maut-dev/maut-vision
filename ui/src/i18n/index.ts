import i18next from "i18next";
import { initReactI18next } from "react-i18next";
import enCommon from "@/locales/en/common.json";
import enErrors from "@/locales/en/errors.json";
import enJobLabels from "@/locales/en/jobLabels.json";
import enProgress from "@/locales/en/progress.json";
import enTrainingLog from "@/locales/en/trainingLog.json";
import enWorker from "@/locales/en/worker.json";
import ruCommon from "@/locales/ru/common.json";
import ruErrors from "@/locales/ru/errors.json";
import ruJobLabels from "@/locales/ru/jobLabels.json";
import ruProgress from "@/locales/ru/progress.json";
import ruTrainingLog from "@/locales/ru/trainingLog.json";
import ruWorker from "@/locales/ru/worker.json";
import type { Language } from "@/api/settings/response-dto/language-response-dto";

export type { Language };

/**
 * Catalogs bundled with the page: the shell (common), error codes (errors) and lines the host sends as codes (lib/coded-text.ts):
 * progress of jobs (progress), system lines of the training log (trainingLog), job labels (jobLabels), worker errors and warnings
 * (worker). Sections add their namespaces with addResourceBundle when their chunk loads, so their texts do not weigh on startup.
 */
export const resources = {
    ru: { common: ruCommon, errors: ruErrors, progress: ruProgress, trainingLog: ruTrainingLog, jobLabels: ruJobLabels, worker: ruWorker },
    en: { common: enCommon, errors: enErrors, progress: enProgress, trainingLog: enTrainingLog, jobLabels: enJobLabels, worker: enWorker },
} as const;

/** Namespaces of codes the host sends (CodedText): a code "<namespace>.<key>" is the key "<namespace>:<key>". */
export const codedNamespaces = ["progress", "trainingLog", "jobLabels", "worker"] as const;

export const defaultNamespace = "common";

/** Language of the browser (WebView2 follows Windows): only when the host cannot be asked (the page outside the application). */
export function browserLanguage(): Language {
    return navigator.language.toLowerCase().startsWith("ru") ? "ru" : "en";
}

/** Initializes the catalogs synchronously, before the first render. */
export function initLocalization(language: Language): void {
    void i18next.use(initReactI18next).init({
        lng: language,
        fallbackLng: false,
        supportedLngs: ["ru", "en"],
        ns: ["common", "errors", ...codedNamespaces],
        defaultNS: defaultNamespace,
        resources,
        partialBundledLanguages: true,
        initAsync: false,
        interpolation: { escapeValue: false },
        returnNull: false,
    });
    document.documentElement.lang = language;
}

/** Switches the interface without a reload; components with useTranslation render again. */
export async function applyLanguage(language: Language): Promise<void> {
    document.documentElement.lang = language;
    if (i18next.language !== language) await i18next.changeLanguage(language);
}

export function currentLanguage(): Language {
    return i18next.language === "en" ? "en" : "ru";
}

export { i18next as i18n };