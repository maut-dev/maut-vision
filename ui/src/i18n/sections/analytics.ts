import en from "@/locales/en/analytics.json";
import ru from "@/locales/ru/analytics.json";
import { i18n } from "@/i18n";

/** The analytics namespace joins the catalogs with the analytics chunk. */
function register(): void {
    i18n.addResourceBundle("ru", "analytics", ru, true, true);
    i18n.addResourceBundle("en", "analytics", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
