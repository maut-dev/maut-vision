import { i18n } from "@/i18n";
import en from "@/locales/en/generation.json";
import ru from "@/locales/ru/generation.json";

/** The generation catalogs join i18next with the chunk of the section (see lib/training/locale.ts). */
function register(): void {
    i18n.addResourceBundle("ru", "generation", ru, true, true);
    i18n.addResourceBundle("en", "generation", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
