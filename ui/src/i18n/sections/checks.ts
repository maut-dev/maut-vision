import en from "@/locales/en/checks.json";
import ru from "@/locales/ru/checks.json";
import { i18n } from "@/i18n";

/** The checks namespace joins the catalogs when a module that reads it loads: the checks chunk and analytics, which shares dataset checks. */
function register(): void {
    i18n.addResourceBundle("ru", "checks", ru, true, true);
    i18n.addResourceBundle("en", "checks", en, true, true);
}

if (i18n.isInitialized) register();
else i18n.on("initialized", register);
