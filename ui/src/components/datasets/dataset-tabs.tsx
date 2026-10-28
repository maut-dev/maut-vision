import type { KeyboardEvent } from "react";
import { Button } from "@/components/ui/button";

export type DatasetTab = "datasets" | "build" | "verify" | "export";

export const datasetTabs: readonly { id: DatasetTab; label: string }[] = [
    { id: "datasets", label: "Датасеты" },
    { id: "build", label: "Сборка" },
    { id: "verify", label: "Проверки" },
    { id: "export", label: "Экспорты" },
];

export const tabId = (tab: DatasetTab) => `dataset-tab-${tab}`;
export const panelId = (tab: DatasetTab) => `dataset-panel-${tab}`;

/**
 * Вкладки раздела «Датасеты» по шаблону WAI-ARIA tablist: фокус только у выбранной вкладки (tabIndex), стрелки влево и вправо
 * переходят по кругу, Home и End — к первой и последней; переход выбирает вкладку и переводит на неё фокус.
 */
export function DatasetTabs({ tab, select }: { tab: DatasetTab; select: (tab: DatasetTab) => void }) {
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
        <div role="tablist" aria-label="Разделы датасетов" className="flex shrink-0 flex-wrap gap-1">
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
