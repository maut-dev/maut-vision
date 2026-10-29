import { i18n } from "@/i18n";
import en from "@/locales/en/models.json";
import ru from "@/locales/ru/models.json";

/** The models catalogs join i18next with the chunk of the section (see lib/training/locale.ts). */
function register(): void {
    i18n.addResourceBundle("ru", "models", ru, true, true);
    i18n.addResourceBundle("en", "models", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
