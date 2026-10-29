import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { subscribeLanguageChanged } from "./api/events/subscriptions";
import { settingsApi } from "./api/settings/settings-api";
import { nativeBridge } from "./api/transport/native-bridge";
import { App } from "./App";
import { UnsavedChangesProvider } from "./components/unsaved-changes";
import { applyLanguage, browserLanguage, currentLanguage, initLocalization, type Language } from "./i18n";
import { installErrorReporting } from "./lib/error-reporting";
import { codedText } from "./lib/coded-text";
import { backgroundReasonText, blockerText, errorText, planWarningText } from "./lib/errors";
import "./index.css";

declare global {
    interface Window {
        /** Texts by code for the native smoke checks, which compare what the page shows with the catalog. */
        mautLocale?: {
            language: () => Language;
            errorText: typeof errorText;
            blockerText: typeof blockerText;
            planWarningText: typeof planWarningText;
            backgroundReasonText: typeof backgroundReasonText;
            codedText: typeof codedText;
        };
    }
}

/** The host decides the language (its setting or Windows); the browser language only when the page runs outside the application. */
async function startupLanguage(): Promise<Language> {
    if (!nativeBridge.available) return browserLanguage();
    try {
        return (await settingsApi.getLanguage(undefined, 3_000)).language;
    } catch {
        return browserLanguage();
    }
}

installErrorReporting();
void startupLanguage().then(language => {
    initLocalization(language);
    subscribeLanguageChanged(event => void applyLanguage(event.data.language));
    window.mautLocale = { language: currentLanguage, errorText, blockerText, planWarningText, backgroundReasonText, codedText };
    createRoot(document.getElementById("root")!).render(<StrictMode><UnsavedChangesProvider><App /></UnsavedChangesProvider></StrictMode>);
});
