import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { Collapsible } from "@/components/layout/collapsible";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Progress } from "@/components/layout/progress";
import { Button } from "@/components/ui/button";
import { isJobActive } from "@/lib/use-asset-imports";
import { count, jobPercent, jobStatusLabels } from "./job-run";

/**
 * The run history of one target (a background set, an image group): a titled list of JobRunRow, the error of the last action and an
 * empty hint. It has no scroll of its own: the owner puts it into a Pane.
 */
export function JobHistory({ title, testId, jobs, error, dismissError, empty, renderRow }: {
    title: string;
    testId: string;
    jobs: readonly JobResponseDto[];
    error?: string;
    dismissError?: () => void;
    empty: string;
    renderRow: (job: JobResponseDto) => ReactNode;
}) {
    return (
        <section aria-label={title} data-testid={testId} className="min-w-0">
            <h3 className="flex items-baseline gap-2 text-sm font-medium">
                {title}
                <span className="text-xs font-normal text-text-3 tabular-nums">{count(jobs.length)}</span>
            </h3>
            {error && <Notice variant="destructive" className="mt-2" onDismiss={dismissError}>{error}</Notice>}
            {jobs.length
                ? <ul className="divide-y divide-line">{jobs.map(renderRow)}</ul>
                : !error && <EmptyState title={empty} className="py-6" />}
        </section>
    );
}

/** Per-file reasons of a run: they unfold inline in the row, without a scroll of their own. */
export function JobIssues({ title, testId, more, children }: { title: string; testId?: string; more?: string; children: ReactNode }) {
    return (
        <Collapsible title={<span className="text-xs font-normal text-text-2">{title}</span>} data-testid={testId}>
            <ul className="list-disc space-y-0.5 pr-3 pb-1 pl-12 text-text-2 break-words">
                {children}
                {more && <li>{more}</li>}
            </ul>
        </Collapsible>
    );
}

/**
 * Running jobs of a target pinned above a form: the state, "P of T" and the bar of each, and a button to the full history. Shown while a
 * job is queued or running, so the work started from the form stays visible.
 */
export function ActiveJobs({ jobs, label, progressLabel, action, className, testId }: {
    jobs: readonly JobResponseDto[];
    label: string;
    progressLabel: string;
    action?: { label: string; onClick: () => void };
    className?: string;
    testId?: string;
}) {
    const { t } = useTranslation();
    const active = jobs.filter(isJobActive);
    if (!active.length) return null;
    return (
        <section aria-label={label} data-testid={testId} className={`surface flex shrink-0 flex-wrap items-center gap-x-4 gap-y-2 px-3 py-2 text-xs ${className ?? ""}`}>
            <div className="min-w-0 flex-1 space-y-2">
                {active.map(job => (
                    <div key={job.id} className="space-y-1" data-active-job-id={job.id}>
                        <div className="flex flex-wrap justify-between gap-x-3">
                            <span className="font-medium">{label} · {job.cancelRequested ? t("jobs.cancelling") : jobStatusLabels[job.status]}</span>
                            {job.total !== null && (
                                <span className="text-text-2 tabular-nums">{t("jobs.progress", { processed: count(job.processed ?? 0), total: count(job.total) })}</span>
                            )}
                        </div>
                        <Progress value={jobPercent(job)} live={job.status === "running"} aria-label={progressLabel} />
                    </div>
                ))}
            </div>
            {action && <Button size="sm" variant="ghost" onClick={action.onClick}>{action.label}</Button>}
        </section>
    );
}
