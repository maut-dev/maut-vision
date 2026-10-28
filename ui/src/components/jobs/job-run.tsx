import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import type { JobResponseDto, JobStatus } from "@/api/jobs/response-dto/job-response-dto";
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

/**
 * Строка истории длинной операции (импорт изображений, подготовка фонов): состояние, время, итог или «P из T», отмена
 * идущей задачи, полоса прогресса и ошибка задачи. Подробности итога владелец передаёт в children.
 */
export function JobRunRow({ job, summary, progressLabel, cancel, children }: {
    job: JobResponseDto;
    /** Итог или ход своими словами; без него у идущей задачи показывается «P из T». */
    summary?: ReactNode;
    progressLabel: string;
    cancel: () => void;
    children?: ReactNode;
}) {
    const { t } = useTranslation();
    const active = isJobActive(job);
    const percent = job.total ? Math.round(((job.processed ?? 0) / job.total) * 100) : job.progress;
    return (
        <li className="space-y-1 py-2 text-xs" data-job-id={job.id} data-status={job.status}>
            <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="flex min-w-0 flex-wrap items-center gap-2">
                    {active && <Loader2 className="size-3.5 animate-spin" aria-hidden />}
                    <span className="font-medium">{job.cancelRequested && active ? t("jobs.cancelling") : jobStatusLabels[job.status]}</span>
                    <span className="text-muted-foreground">{formatDate(job.createdAt)}</span>
                    {summary ?? (active && job.total !== null && (
                        <span className="tabular-nums text-muted-foreground">{t("jobs.progress", { processed: count(job.processed ?? 0), total: count(job.total) })}</span>
                    ))}
                </span>
                {active && <Button size="xs" variant="ghost" disabled={job.cancelRequested} onClick={cancel}>{t("common.cancelAction")}</Button>}
            </div>
            {active && (
                <div className="h-1 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={progressLabel} aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent ?? undefined}>
                    <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"} style={percent === null ? undefined : { width: `${percent}%` }} />
                </div>
            )}
            {!active && job.status !== "succeeded" && job.error && <p className="text-destructive">{jobErrorText(job.error)}</p>}
            {children}
        </li>
    );
}
