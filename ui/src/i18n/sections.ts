import { i18n } from "@/i18n";

/**
 * Adds the catalogs of a section namespace to i18next. Each namespace has one module `i18n/sections/<namespace>.ts` that calls this,
 * and every module that reads the namespace imports that module for its side effect, so the texts arrive with the chunk that shows
 * them. A module of the page (the overview, the catalog) is evaluated before initLocalization: then the bundles join as soon as i18next
 * is initialized, before the first render.
 */
export function registerSection(namespace: string, catalogs: { ru: object; en: object }): void {
    const add = () => {
        i18n.addResourceBundle("ru", namespace, catalogs.ru, true, true);
        i18n.addResourceBundle("en", namespace, catalogs.en, true, true);
    };
    if (i18n.isInitialized) add();
    else i18n.on("initialized", add);
}
