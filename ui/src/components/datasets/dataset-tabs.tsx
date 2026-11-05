import { i18n } from "@/i18n";

export type DatasetTab = "datasets" | "build" | "verify" | "export";

/** Tabs of the section "Datasets" with labels in the current language (read at render time). */
export const datasetTabs: readonly { id: DatasetTab; label: string }[] = [
    { id: "datasets", get label() { return i18n.t("datasets:tabs.datasets"); } },
    { id: "build", get label() { return i18n.t("datasets:tabs.build"); } },
    { id: "verify", get label() { return i18n.t("datasets:tabs.verify"); } },
    { id: "export", get label() { return i18n.t("datasets:tabs.export"); } },
];

/** Stable ids of a tab and its panel: "Open dataset" and a started check or export move the focus to the tab by its id. */
export const tabId = (tab: DatasetTab) => `dataset-tab-${tab}`;
export const panelId = (tab: DatasetTab) => `dataset-panel-${tab}`;
