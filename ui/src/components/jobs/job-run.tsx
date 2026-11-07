import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import type { JobResponseDto, JobStatus } from "@/api/jobs/response-dto/job-response-dto";
import { Progress } from "@/components/layout/progress";
import { Button } from "@/components/ui/button";
import { i18n } from "@/i18n";
import { jobErrorText } from "@/lib/errors";
import { formatDate, formatNumber } from "@/lib/format";
import { isJobActive } from "@/lib/use-asset-imports";

/** Labels of job statuses in the current language (read at render time). */
export const jobStatusLabels: Readonly<Record<JobStatus, string>> = {
    get queued() { return i18n.t("jobs.status.queued"); },
    get running() { return i18n.t("jobs.status.running"); },
    get succeeded() { return i18n.t("jobs.status.succeeded"); },
    get failed() { return i18n.t("jobs.status.failed"); },
    get cancelled() { return i18n.t("jobs.status.cancelled"); },
    get interrupted() { return i18n.t("jobs.status.interrupted"); },
    get blocked() { return i18n.t("jobs.status.blocked"); },
};

export const count = (value: number) => formatNumber(value);

/** Percent of a job: processed of total when the total is known, otherwise the progress it reports; null — indeterminate. */
export function jobPercent(job: JobResponseDto): number | null {
    return job.total ? Math.round(((job.processed ?? 0) / job.total) * 100) : job.progress;
}

/**
 * A history row of a long operation (image import, background preparation): state, time, the result or "P of T", cancelling
 * a running job, the progress bar and the job error. The owner passes details of the result in children.
 */
export function JobRunRow({ job, summary, progressLabel, cancel, children }: {
    job: JobResponseDto;
    /** The result or the progress in its own words; without it a running job shows "P of T". */
    summary?: ReactNode;
    progressLabel: string;
    cancel: () => void;
    children?: ReactNode;
}) {
    const { t } = useTranslation();
    const active = isJobActive(job);
    return (
        <li className="space-y-1.5 py-2.5 text-xs" data-job-id={job.id} data-status={job.status}>
            <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
                    {active && <Loader2 className="size-3.5 animate-spin text-text-2" aria-hidden />}
                    <span className="font-medium">{job.cancelRequested && active ? t("jobs.cancelling") : jobStatusLabels[job.status]}</span>
                    <span className="text-text-2">{formatDate(job.createdAt)}</span>
                    {summary ?? (active && job.total !== null && (
                        <span className="tabular-nums text-text-2">{t("jobs.progress", { processed: count(job.processed ?? 0), total: count(job.total) })}</span>
                    ))}
                </span>
                {active && <Button size="xs" variant="ghost" disabled={job.cancelRequested} onClick={cancel}>{t("common.cancelAction")}</Button>}
            </div>
            {active && <Progress value={jobPercent(job)} live={job.status === "running"} aria-label={progressLabel} />}
            {!active && job.status !== "succeeded" && job.error && <p className="text-destructive">{jobErrorText(job.error)}</p>}
            {children}
        </li>
    );
}
