import type { KeyboardEvent } from "react";
import { useTranslation } from "react-i18next";
import { Button } from "@/components/ui/button";
import { i18n } from "@/i18n";

export type TrainingTab = "settings" | "current" | "history";

/** Tabs with labels in the current language (read at render time). */
export const trainingTabs: readonly { id: TrainingTab; label: string }[] = [
    { id: "settings", get label() { return i18n.t("training:tabs.settings"); } },
    { id: "current", get label() { return i18n.t("training:tabs.current"); } },
    { id: "history", get label() { return i18n.t("training:tabs.history"); } },
];

export const trainingTabId = (tab: TrainingTab) => `training-tab-${tab}`;
export const trainingPanelId = (tab: TrainingTab) => `training-panel-${tab}`;

/** Tabs of the section "Training" by the tablist pattern, as in datasets: one tab stop, arrows wrap around, Home and End. */
export function TrainingTabs({ tab, select }: { tab: TrainingTab; select: (tab: TrainingTab) => void }) {
    const { t } = useTranslation("training");
    function onKeyDown(event: KeyboardEvent, index: number) {
        const last = trainingTabs.length - 1;
        const next = event.key === "ArrowRight" ? (index + 1) % trainingTabs.length
            : event.key === "ArrowLeft" ? (index + last) % trainingTabs.length
                : event.key === "Home" ? 0 : event.key === "End" ? last : null;
        if (next === null) return;
        event.preventDefault();
        select(trainingTabs[next].id);
        document.getElementById(trainingTabId(trainingTabs[next].id))?.focus();
    }

    return (
        <div role="tablist" aria-label={t("tabs.label")} className="flex shrink-0 flex-wrap gap-1 border-b pb-2">
            {trainingTabs.map((item, index) => (
                <Button
                    key={item.id}
                    id={trainingTabId(item.id)}
                    role="tab"
                    data-testid={`training-tab-${item.id}`}
                    tabIndex={tab === item.id ? 0 : -1}
                    aria-selected={tab === item.id}
                    aria-controls={trainingPanelId(item.id)}
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
