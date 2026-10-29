import en from "@/locales/en/overview.json";
import ru from "@/locales/ru/overview.json";
import { i18n } from "@/i18n";

/**
 * The overview namespace. The overview is part of the page, so this module loads before the catalogs initialize: the namespace joins them
 * as soon as they do, before the first render.
 */
function register(): void {
    i18n.addResourceBundle("ru", "overview", ru, true, true);
    i18n.addResourceBundle("en", "overview", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
