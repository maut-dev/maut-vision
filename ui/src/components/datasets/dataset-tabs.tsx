import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { i18n } from "@/i18n";

export type DatasetTab = "datasets" | "build" | "verify" | "export";

/** Tabs with labels in the current language (read at render time). */
export const datasetTabs: readonly { id: DatasetTab; label: string }[] = [
    { id: "datasets", get label() { return i18n.t("datasets:tabs.datasets"); } },
    { id: "build", get label() { return i18n.t("datasets:tabs.build"); } },
    { id: "verify", get label() { return i18n.t("datasets:tabs.verify"); } },
    { id: "export", get label() { return i18n.t("datasets:tabs.export"); } },
];

export const tabId = (tab: DatasetTab) => `dataset-tab-${tab}`;
export const panelId = (tab: DatasetTab) => `dataset-panel-${tab}`;

/**
 * Tabs of the section "Datasets" by the WAI-ARIA tablist pattern: only the chosen tab takes focus (tabIndex), left and right arrows wrap
 * around, Home and End go to the first and the last; a move chooses the tab and focuses it.
 */
export function DatasetTabs({ tab, select }: { tab: DatasetTab; select: (tab: DatasetTab) => void }) {
    const { t } = useTranslation("datasets");
    function onKeyDown(event: KeyboardEvent, index: number) {
        const last = datasetTabs.length - 1;
        const next = event.key === "ArrowRight" ? (index + 1) % datasetTabs.length
            : event.key === "ArrowLeft" ? (index + last) % datasetTabs.length
                : event.key === "Home" ? 0 : event.key === "End" ? last : null;
        if (next === null) return;
        event.preventDefault();
        select(datasetTabs[next].id);
        document.getElementById(tabId(datasetTabs[next].id))?.focus();
    }

    return (
        <div role="tablist" aria-label={t("tabs.label")} className="flex shrink-0 flex-wrap gap-1">
            {datasetTabs.map((item, index) => (
                <Button
                    key={item.id}
                    id={tabId(item.id)}
                    role="tab"
                    data-testid={`datasets-tab-${item.id}`}
                    tabIndex={tab === item.id ? 0 : -1}
                    aria-selected={tab === item.id}
                    aria-controls={panelId(item.id)}
                    variant={tab === item.id ? "secondary" : "ghost"}
                    onClick={() => select(item.id)}
                    onKeyDown={event => onKeyDown(event, index)}
                >
                    {item.label}
                </Button>
            ))}
        </div>
    );
}
