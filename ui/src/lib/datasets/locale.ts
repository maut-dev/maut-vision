import { i18n } from "@/i18n";
import en from "@/locales/en/datasets.json";
import ru from "@/locales/ru/datasets.json";

/** The datasets catalogs join i18next with the chunk of the section (see lib/training/locale.ts). */
function register(): void {
    i18n.addResourceBundle("ru", "datasets", ru, true, true);
    i18n.addResourceBundle("en", "datasets", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
