import { Hammer, Loader2 } from "lucide-react";
import type { DatasetPlanResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Button } from "@/components/ui/button";
import { classOrderText, count, skipReasons } from "@/lib/datasets/dataset-view";

/**
 * Карточка плана «Проверить перед сборкой»: «Train · Validation · Test · пропущено N» с причинами пропуска, кадр и порядок
 * классов, предупреждения host (недостаточно источников, смешанный источник, объекты как негативы, одинаковые пиксели в
 * разных частях). «Собрать датасет» недоступна, если сборка невозможна (buildable), источников недостаточно или форма
 * изменилась после проверки (stale) — тогда нужно проверить снова.
 */
export function DatasetPlanCard({ plan, stale, busy, building, build }: {
    plan: DatasetPlanResponseDto;
    stale: boolean;
    busy: boolean;
    building: boolean;
    build: () => void;
}) {
    const reasons = skipReasons(plan.skipped);
    const blocked = !plan.buildable || !plan.sufficientSamples;
    return (
        <section className="space-y-3 rounded-md border bg-card p-5" aria-label="План сборки" data-testid="dataset-plan-card" data-plan-id={plan.id}
            data-buildable={plan.buildable && plan.sufficientSamples ? "true" : "false"} data-stale={stale ? "true" : undefined}>
            <h3 className="font-medium break-words">{plan.specification.name} · {blocked ? "Сборка невозможна" : "Готов к сборке"}</h3>
            <p className="text-sm text-muted-foreground tabular-nums" data-testid="dataset-plan-counts">
                Train {count(plan.counts.train)} · Validation {count(plan.counts.val)} · Test {count(plan.counts.test)} · пропущено {count(plan.skipped.total)}
            </p>
            {reasons.length > 0 && (
                <ul className="list-disc space-y-0.5 pl-5 text-xs text-muted-foreground" aria-label="Причины пропуска" data-testid="dataset-plan-skips">
                    {reasons.map(reason => <li key={reason.key} data-reason={reason.key}>{reason.label}: {count(reason.count)}</li>)}
                </ul>
            )}
            <p className="text-xs text-muted-foreground">
                Кадр {plan.width} × {plan.height} · классы: {classOrderText({ classMap: plan.classes })} · рамок: {count(plan.composition.boxes)} · негативов: {count(plan.composition.negatives)}
            </p>
            {plan.warnings.map(warning => (
                <p key={warning.code} role="status" data-warning={warning.code}
                    className={`text-xs ${warning.code === "datasets.split_collision" ? "text-destructive" : "text-amber-700 dark:text-amber-400"}`}>
                    {warning.message}
                </p>
            ))}
            {stale && <p className="text-xs text-muted-foreground" data-testid="dataset-plan-stale">Настройки формы изменились после проверки. Нажмите «Проверить перед сборкой» ещё раз.</p>}
            <Button disabled={busy || stale || blocked} data-testid="dataset-build" onClick={build}>
                {building ? <Loader2 className="animate-spin" aria-hidden /> : <Hammer />}Собрать датасет
            </Button>
        </section>
    );
}
