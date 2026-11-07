import { i18n } from "@/i18n";

export type TrainingTab = "settings" | "current" | "history";

/** Tabs of the section "Training" with labels in the current language (read at render time). */
export const trainingTabs: readonly { id: TrainingTab; label: string }[] = [
    { id: "settings", get label() { return i18n.t("training:tabs.settings"); } },
    { id: "current", get label() { return i18n.t("training:tabs.current"); } },
    { id: "history", get label() { return i18n.t("training:tabs.history"); } },
];

/** Stable ids of a tab and its panel: a started run and "Run passport" move the focus to the tab by its id. */
export const trainingTabId = (tab: TrainingTab) => `training-tab-${tab}`;
export const trainingPanelId = (tab: TrainingTab) => `training-panel-${tab}`;
