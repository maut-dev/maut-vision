import { i18n } from "@/i18n";
import en from "@/locales/en/catalog.json";
import ru from "@/locales/ru/catalog.json";

/**
 * Adds the catalogs of a section namespace. A module of the section imports its locale file, so the texts arrive with the code that
 * shows them. The catalog section is part of the main chunk and is evaluated before initLocalization: then the bundle is added as soon
 * as i18next is initialized, before the first render.
 */
export function addSectionCatalogs(namespace: string, catalogs: { ru: object; en: object }): void {
    const add = () => {
        i18n.addResourceBundle("ru", namespace, catalogs.ru, true, true);
        i18n.addResourceBundle("en", namespace, catalogs.en, true, true);
    };
    if (i18n.isInitialized) add();
    else i18n.on("initialized", add);
}

addSectionCatalogs("catalog", { ru, en });
