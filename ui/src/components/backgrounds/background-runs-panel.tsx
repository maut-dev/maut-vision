import { useTranslation } from "react-i18next";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { parseImportBackgroundsResult } from "@/api/projects/backgrounds/response-dto/import-backgrounds-result-dto";
import { JobHistory, JobIssues } from "@/components/jobs/job-history";
import { count, JobRunRow } from "@/components/jobs/job-run";
import { blurSummary } from "@/lib/backgrounds/background-text";
import { isJobActive } from "@/lib/use-asset-imports";
import type { BackgroundRunsState } from "@/lib/use-background-runs";
import { backgroundReasonText } from "@/lib/errors";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/backgrounds";

/**
 * "Background preparation": the history of this set only: the progress "processed P of T", the result (added of the target,
 * duplicates, similar, rejected, the blur with its seed), skip reasons per file and cancelling. No scroll of its own: it sits in a Pane.
 */
export function BackgroundRuns({ runs }: { runs: BackgroundRunsState }) {
    const { t } = useTranslation("backgrounds");
    return (
        <JobHistory
            title={t("runs.title")}
            testId="background-runs"
            jobs={runs.jobs}
            error={runs.error}
            dismissError={() => runs.setError(undefined)}
            empty={t("runs.empty")}
            renderRow={job => <RunRow key={job.id} job={job} cancel={() => void runs.cancel(job)} />}
        />
    );
}

function RunRow({ job, cancel }: { job: JobResponseDto; cancel: () => void }) {
    const { t } = useTranslation("backgrounds");
    const result = parseImportBackgroundsResult(job);
    const skipped = result ? result.duplicates + result.similar + result.rejected : 0;
    const summary = result ? (
        <span className="tabular-nums text-text-2" data-testid="run-summary">
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
        <span className="tabular-nums text-text-2" data-testid="run-progress">{t("runs.processed", { processed: count(job.processed ?? 0), total: count(job.total) })}</span>
    ) : undefined;
    return (
        <JobRunRow job={job} summary={summary} progressLabel={t("runs.progress")} cancel={cancel}>
            {!!result?.issues.length && (
                <JobIssues
                    title={t("runs.issues", { number: count(skipped) })}
                    testId="run-issues"
                    more={skipped > result.issues.length ? t("runs.more", { number: count(skipped - result.issues.length) }) : undefined}
                >
                    {result.issues.map((issue, index) => (
                        <li key={`${issue.sourceIndex}-${index}`}>
                            {issue.name}{issue.timestampMs !== null && ` · ${t("seconds", { seconds: formatNumber(issue.timestampMs / 1000, { maximumFractionDigits: 3 }) })}`} · {backgroundReasonText(issue.reason, issue.width, issue.height, result.targetSize)}
                        </li>
                    ))}
                </JobIssues>
            )}
        </JobRunRow>
    );
}
