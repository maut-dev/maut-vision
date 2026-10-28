import type { KeyboardEvent } from "react";
import { Button } from "@/components/ui/button";

export type TrainingTab = "settings" | "current" | "history";

export const trainingTabs: readonly { id: TrainingTab; label: string }[] = [
    { id: "settings", label: "Настройки" },
    { id: "current", label: "Текущий запуск" },
    { id: "history", label: "История обучения" },
];

export const trainingTabId = (tab: TrainingTab) => `training-tab-${tab}`;
export const trainingPanelId = (tab: TrainingTab) => `training-panel-${tab}`;

/** Вкладки раздела «Обучение» по шаблону tablist, как у датасетов: одна точка табуляции, стрелки по кругу, Home и End. */
export function TrainingTabs({ tab, select }: { tab: TrainingTab; select: (tab: TrainingTab) => void }) {
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
        <div role="tablist" aria-label="Обучение" className="flex shrink-0 flex-wrap gap-1 border-b pb-2">
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
