import { FolderOpen, Loader2, RotateCcw, Trash2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import { datasetTexts, integrityLabels } from "@/api/projects/datasets/dataset-rules";
import {
    parseDatasetBuildResult,
    parseDatasetExportResult,
    parseDatasetVerifyResult,
    type DatasetOperationResponseDto,
} from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { jobStatusLabels } from "@/components/jobs/job-run";
import { Button } from "@/components/ui/button";
import {
    count,
    duration,
    isOperationActive,
    issueRoleLabels,
    operationEndText,
    operationProgress,
    operationTitles,
    splitLabel,
} from "@/lib/datasets/dataset-view";
import { formatDate, megabytes } from "@/lib/format";
import { errorText } from "@/lib/errors";

/**
 * An operation card in the tabs "Build", "Checks", "Exports": state, dataset, date, "Images built / Files checked / Images exported:
 * p of t" with a bar by job.changed events, duration, "Issues found (n)", the export file; "Cancel" for a running one, "Set up a
 * repeat" / "Retry" / "Save again" and "Delete from history" for a finished one, "Open dataset" when the version is in the list.
 * version — the current version (name, archive, integrity).
 */
export function DatasetOperationCard({ operation, version, busy, cancel, repeat, remove, openDataset }: {
    operation: DatasetOperationResponseDto;
    version: DatasetVersionResponseDto | undefined;
    busy: boolean;
    cancel: () => void;
    repeat: () => void;
    remove: () => void;
    openDataset: () => void;
}) {
    const { t } = useTranslation("datasets");
    const { job, kind } = operation;
    const active = isOperationActive(job);
    const progress = operationProgress(operation);
    const status = job.cancelRequested && active ? t("operation.cancelling") : jobStatusLabels[job.status];
    const name = version?.name ?? operation.versionName;
    const elapsed = duration(job);
    const endText = operationEndText(operation);
    const build = parseDatasetBuildResult(job);
    const verify = parseDatasetVerifyResult(job);
    const exported = parseDatasetExportResult(job);
    const fileName = exported?.fileName ?? operation.fileName;
    const archived = !!version?.archivedAt;
    const repeatLabel = kind === "build" ? t("operation.repeatBuild") : kind === "export" && job.status === "succeeded" ? t("operation.saveAgain") : t("operation.retry");
    const subtitle = [kind === "build" ? operationTitles.build : name, ...(!version && !active && job.status === "succeeded" ? [t("operation.versionMissing")] : []),
        formatDate(job.createdAt)];
    const times = job.startedAt ? [t("operation.started", { date: formatDate(job.startedAt) }),
        ...(job.finishedAt ? [t("operation.finished", { date: formatDate(job.finishedAt) })] : []), ...(elapsed ? [t("operation.duration", { duration: elapsed })] : [])] : [];
    const repeatBlocked = kind === "build" ? !operation.specification : !version || archived || (kind === "export" && version.integrityState === "corrupt");
    const canOpen = !!version && (kind !== "build" || job.status === "succeeded");
    return (
        <article className="space-y-3 rounded-md border bg-card p-4" data-dataset-operation-id={job.id} data-kind={kind} data-status={job.status}
            aria-label={`${operationTitles[kind]}: ${name}`}>
            <header className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                    <h3 className="flex items-center gap-2 font-medium break-words" data-testid="operation-title">
                        {active && <Loader2 className="size-3.5 shrink-0 animate-spin" aria-hidden />}
                        {kind === "build" ? name : operationTitles[kind]} · {status}
                    </h3>
                    <p className="mt-1 text-sm break-words text-muted-foreground">
                        {subtitle.join(" · ")}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {canOpen && (
                        <Button variant="outline" size="sm" data-testid="operation-open-dataset" aria-label={t("operation.openDatasetLabel", { name })} onClick={openDataset}>
                            <FolderOpen />{t("operation.openDataset")}
                        </Button>
                    )}
                    {active ? (
                        <Button variant="outline" size="sm" disabled={busy || job.cancelRequested} data-testid="operation-cancel" onClick={cancel}>
                            {job.cancelRequested ? t("operation.cancellingAction") : t("operation.cancel")}
                        </Button>
                    ) : (
                        <>
                            <Button variant="outline" size="sm" disabled={busy || repeatBlocked} data-testid="operation-repeat" aria-label={`${repeatLabel}: ${name}`} onClick={repeat}>
                                <RotateCcw />{repeatLabel}
                            </Button>
                            <Button variant="ghost" size="sm" disabled={busy} data-testid="operation-delete" aria-label={t("operation.deleteLabel", { name })} onClick={remove}>
                                <Trash2 />{t("operation.delete")}
                            </Button>
                        </>
                    )}
                </div>
            </header>
            <p className="text-sm tabular-nums" data-testid="operation-progress-text">
                {progress.total !== null
                    ? t("operation.progressOf", { label: progress.label, processed: count(progress.processed), total: count(progress.total) })
                    : t("operation.progress", { label: progress.label, processed: count(progress.processed) })}
                {progress.percent !== null && active && ` · ${progress.percent}%`}
            </p>
            {(active || job.status === "succeeded") && (
                <div className="h-1.5 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("operation.progressLabel", { title: operationTitles[kind] })}
                    aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress.percent ?? undefined} data-testid="operation-progress">
                    <div className={progress.percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                        style={progress.percent === null ? undefined : { width: `${progress.percent}%` }} />
                </div>
            )}
            {kind === "export" && fileName && (
                <p className="text-sm break-all" data-testid="operation-file">{t("operation.file", { name: fileName })}{exported && exported.bytes > 0 && ` · ${megabytes(exported.bytes)}`}</p>
            )}
            {kind === "export" && active && <p role="status" className="text-xs text-muted-foreground">{t("operation.exportRunning")}</p>}
            {kind === "verify" && verify?.state && (
                <p className={`text-sm ${verify.state === "corrupt" ? "text-destructive" : ""}`} data-testid="operation-integrity">{t("operation.integrity", { state: integrityLabels[verify.state] })}</p>
            )}
            {times.length > 0 && <p className="text-xs text-muted-foreground">{times.join(" · ")}</p>}
            {verify && verify.issueCount > 0 && (
                <div className="space-y-2 rounded-md border p-3 text-sm" data-testid="operation-issues">
                    <h4 className="font-medium">{t("operation.issues", { number: count(verify.issueCount) })}</h4>
                    <ul className="max-h-40 space-y-1 overflow-auto" aria-label={t("operation.issuesLabel")}>
                        {verify.issues.map((issue, index) => (
                            <li key={`${issue.role}:${issue.sampleId}:${index}`} className="break-words">
                                {[issueRoleLabels[issue.role], ...(issue.sampleId ? [t("operation.issueFrame", { id: issue.sampleId })] : []),
                                    ...(issue.split ? [splitLabel(issue.split)] : []), errorText(issue.code)].join(" · ")}
                            </li>
                        ))}
                    </ul>
                    {verify.issueCount > verify.issues.length && <p className="text-xs text-muted-foreground">{t("operation.issuesShown", { number: count(verify.issues.length) })}</p>}
                    <p className="text-xs text-muted-foreground">{datasetTexts.integrityHelp}</p>
                </div>
            )}
            {endText && <p role="alert" className="text-sm break-words text-destructive" data-testid="operation-error">{endText}</p>}
            <details className="text-xs text-muted-foreground">
                <summary className="cursor-pointer">{kind === "build" ? t("operation.buildDetails") : t("operation.details")}</summary>
                <div className="mt-2 space-y-2">
                    {kind === "build" && operation.specification && (
                        <>
                            <p>
                                {t("operation.buildSpec", {
                                    seed: operation.specification.seed, groups: operation.specification.groupIds.length, classes: operation.specification.classIds.length,
                                    val: Math.round(operation.specification.valRatio * 100), test: Math.round(operation.specification.testRatio * 100),
                                })}
                            </p>
                            <p>{t("operation.repeatNote")}</p>
                        </>
                    )}
                    {build && (
                        <p>
                            {t("operation.buildResult", {
                                train: count(build.counts.train), val: count(build.counts.val), test: count(build.counts.test),
                                linked: count(build.linked), copied: count(build.copied), converted: count(build.converted),
                            })}
                        </p>
                    )}
                    {kind === "verify" && <p>{t("operation.verifyNote")}</p>}
                    {kind === "export" && <p>{t("operation.exportNote")}</p>}
                    {job.status === "succeeded" && kind === "verify" && <p>{t("operation.verifyDone")}</p>}
                    {job.status === "succeeded" && kind === "export" && <p>{t("operation.exportDone")}</p>}
                    <p className="break-all">{t("operation.version", { id: operation.versionId })}</p>
                </div>
            </details>
        </article>
    );
}
