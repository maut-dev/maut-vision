import { i18n } from "@/i18n";
import en from "@/locales/en/training.json";
import ru from "@/locales/ru/training.json";

/**
 * The training catalogs join i18next when the code that shows them loads (the training section, or a section that borrows parameter
 * labels or device names). Before the page initializes i18next the bundles wait for its "initialized" event.
 */
function register(): void {
    i18n.addResourceBundle("ru", "training", ru, true, true);
    i18n.addResourceBundle("en", "training", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);

/** A text of the training catalog by a key built at run time (a parameter, a stage); keys written in code use the typed t. */
export const trainingText = (key: string, options?: Record<string, unknown>): string =>
    (i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string)(`training:${key}`, options);
