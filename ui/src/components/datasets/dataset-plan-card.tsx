import { Hammer, Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { DatasetPlanResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Button } from "@/components/ui/button";
import { classOrderText, count, skipReasons } from "@/lib/datasets/dataset-view";
import { planWarningText } from "@/lib/errors";

/**
 * The plan card of "Check before building": "Train · Validation · Test · skipped N" with skip reasons, the frame and the class order,
 * host warnings (not enough sources, a mixed source, objects as negatives, the same pixels in different splits). "Build dataset" is
 * unavailable when the build is impossible (buildable), sources are insufficient or the form changed after the check (stale) — then
 * check again.
 */
export function DatasetPlanCard({ plan, stale, busy, building, build }: {
    plan: DatasetPlanResponseDto;
    stale: boolean;
    busy: boolean;
    building: boolean;
    build: () => void;
}) {
    const { t } = useTranslation("datasets");
    const reasons = skipReasons(plan.skipped);
    const blocked = !plan.buildable || !plan.sufficientSamples;
    return (
        <section className="space-y-3 rounded-md border bg-card p-5" aria-label={t("plan.label")} data-testid="dataset-plan-card" data-plan-id={plan.id}
            data-buildable={plan.buildable && plan.sufficientSamples ? "true" : "false"} data-stale={stale ? "true" : undefined}>
            <h3 className="font-medium break-words">{plan.specification.name} · {blocked ? t("plan.blocked") : t("plan.ready")}</h3>
            <p className="text-sm text-muted-foreground tabular-nums" data-testid="dataset-plan-counts">
                {t("plan.counts", { train: count(plan.counts.train), val: count(plan.counts.val), test: count(plan.counts.test), skipped: count(plan.skipped.total) })}
            </p>
            {reasons.length > 0 && (
                <ul className="list-disc space-y-0.5 pl-5 text-xs text-muted-foreground" aria-label={t("plan.skips")} data-testid="dataset-plan-skips">
                    {reasons.map(reason => <li key={reason.key} data-reason={reason.key}>{t("plan.skip", { label: reason.label, number: count(reason.count) })}</li>)}
                </ul>
            )}
            <p className="text-xs text-muted-foreground">
                {t("plan.frame", { width: plan.width, height: plan.height, classes: classOrderText({ classMap: plan.classes }), boxes: count(plan.composition.boxes), negatives: count(plan.composition.negatives) })}
            </p>
            {plan.warnings.map(warning => (
                <p key={warning.code} role="status" data-warning={warning.code}
                    className={`text-xs ${warning.code === "datasets.split_collision" ? "text-destructive" : "text-amber-700 dark:text-amber-400"}`}>
                    {planWarningText(warning)}
                </p>
            ))}
            {stale && <p className="text-xs text-muted-foreground" data-testid="dataset-plan-stale">{t("plan.stale")}</p>}
            <Button disabled={busy || stale || blocked} data-testid="dataset-build" onClick={build}>
                {building ? <Loader2 className="animate-spin" aria-hidden /> : <Hammer />}{t("plan.build")}
            </Button>
        </section>
    );
}
