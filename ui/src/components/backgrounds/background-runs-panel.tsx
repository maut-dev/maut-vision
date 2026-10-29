import { ChevronDown, ChevronRight, Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { parseImportBackgroundsResult } from "@/api/projects/backgrounds/response-dto/import-backgrounds-result-dto";
import { count, JobRunRow } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import { blurSummary } from "@/lib/backgrounds/background-text";
import { isJobActive } from "@/lib/use-asset-imports";
import type { BackgroundRunsState } from "@/lib/use-background-runs";
import { backgroundReasonText } from "@/lib/errors";
import { formatNumber } from "@/lib/format";
import "./locale";

/**
 * "Background preparation": the history of this set only: the progress "processed P of T", the result (added of the target,
 * duplicates, similar, rejected, the blur with its seed), skip reasons per file and cancelling. It folds so the gallery stays visible.
 */
export function BackgroundRunsPanel({ runs, open, setOpen }: { runs: BackgroundRunsState; open: boolean; setOpen: (open: boolean) => void }) {
    const { t } = useTranslation("backgrounds");
    if (!runs.jobs.length && !runs.error) return null;
    const active = runs.jobs.filter(isJobActive).length;
    return (
        <section className="flex max-h-40 shrink-0 flex-col overflow-hidden rounded-md border bg-card" aria-label={t("runs.title")} data-testid="background-runs">
            <button
                type="button"
                className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm font-medium outline-none hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring"
                aria-expanded={open}
                aria-controls="background-runs-list"
                data-testid="background-runs-toggle"
                onClick={() => setOpen(!open)}
            >
                {open ? <ChevronDown className="size-4" aria-hidden /> : <ChevronRight className="size-4" aria-hidden />}
                {t("runs.title")}
                <span className="text-xs font-normal text-muted-foreground tabular-nums">({runs.jobs.length})</span>
                {active > 0 && <span className="flex items-center gap-1 text-xs font-normal text-muted-foreground"><Loader2 className="size-3.5 animate-spin" aria-hidden />{t("runs.active", { number: active })}</span>}
            </button>
            <div id="background-runs-list" hidden={!open} className="min-h-0 overflow-y-auto px-3 pb-2">
                {runs.error && (
                    <p role="alert" className="flex items-center justify-between gap-2 text-xs text-destructive">
                        {runs.error}
                        <Button variant="ghost" size="xs" onClick={() => runs.setError(undefined)}>{t("section.hide")}</Button>
                    </p>
                )}
                <ul className="divide-y">
                    {runs.jobs.map(job => <RunRow key={job.id} job={job} cancel={() => void runs.cancel(job)} />)}
                </ul>
            </div>
        </section>
    );
}

function RunRow({ job, cancel }: { job: JobResponseDto; cancel: () => void }) {
    const { t } = useTranslation("backgrounds");
    const result = parseImportBackgroundsResult(job);
    const skipped = result ? result.duplicates + result.similar + result.rejected : 0;
    const summary = result ? (
        <span className="tabular-nums text-muted-foreground" data-testid="run-summary">
            {[
                result.requestedCount !== null
                    ? t("runs.addedOf", { added: count(result.added), requested: count(result.requestedCount) })
                    : t("runs.added", { added: count(result.added) }),
                t("runs.duplicates", { number: count(result.duplicates) }),
                result.similar > 0 ? t("runs.similar", { number: count(result.similar) }) : null,
                t("runs.rejected", { number: count(result.rejected) }),
            ].filter(Boolean).join(", ")}
            {result.processed < result.total && ` · ${t("runs.processed", { processed: count(result.processed), total: count(result.total) })}`}
            {result.blur && ` · ${blurSummary(result.blur)}`}
        </span>
    ) : isJobActive(job) && job.total !== null ? (
        <span className="tabular-nums text-muted-foreground" data-testid="run-progress">{t("runs.processed", { processed: count(job.processed ?? 0), total: count(job.total) })}</span>
    ) : undefined;
    return (
        <JobRunRow job={job} summary={summary} progressLabel={t("runs.progress")} cancel={cancel}>
            {!!result?.issues.length && (
                <details data-testid="run-issues">
                    <summary className="cursor-pointer text-muted-foreground">{t("runs.issues", { number: count(skipped) })}</summary>
                    <ul className="mt-1 max-h-24 list-disc space-y-0.5 overflow-y-auto pl-5">
                        {result.issues.map((issue, index) => (
                            <li key={`${issue.sourceIndex}-${index}`} className="break-words">
                                {issue.name}{issue.timestampMs !== null && ` · ${t("seconds", { seconds: formatNumber(issue.timestampMs / 1000, { maximumFractionDigits: 3 }) })}`} · {backgroundReasonText(issue.reason, issue.width, issue.height, result.targetSize)}
                            </li>
                        ))}
                        {skipped > result.issues.length && <li>{t("runs.more", { number: count(skipped - result.issues.length) })}</li>}
                    </ul>
                </details>
            )}
        </JobRunRow>
    );
}
