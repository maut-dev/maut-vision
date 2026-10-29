import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Settings } from "lucide-react";
import type { BaseModelResponseDto } from "@/api/base-models/response-dto/base-model-response-dto";
import type { ActiveJobResponseDto } from "@/api/jobs/response-dto/active-jobs-page-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import "@/i18n/sections/overview";
import { formatDate, formatMetric, formatNumber } from "@/lib/format";
import { activeJobKinds, activeJobPercent, activeJobState, activeJobTitle, exportedVersionNumber, type JobSection } from "@/lib/overview/active-job-view";
import { useOverview } from "@/lib/use-overview";
import { OverviewHardware } from "./overview-hardware";

/** A navigation from the overview: the section and the intent (a training run, a version in Models, the Dataset mode of checks). */
export interface OverviewTarget {
    section: JobSection;
    runId?: string;
    versionId?: string;
    checksMode?: "dataset";
}

/**
 * The Overview section: the project settings, project data, running jobs, the latest models and the computer. Project data comes from
 * useOverview by events, without polling and without a Refresh button; only the hardware is polled while the section is open and the
 * window is visible.
 */
export function OverviewSection({ project, baseModel, edit, openCatalog, open }: {
    project: ProjectResponseDto;
    baseModel?: BaseModelResponseDto;
    edit: () => void;
    openCatalog: () => void;
    open: (target: OverviewTarget) => void;
}) {
    const { t } = useTranslation("overview");
    const { t: tc } = useTranslation();
    const projectId = project.id;
    const data = useOverview(projectId);
    const weightsMissing = baseModel !== undefined && baseModel.status !== "installed";
    const state = data.state;
    const [opening, setOpening] = useState(false);

    async function openJob(item: ActiveJobResponseDto) {
        const { job } = item;
        const kind = activeJobKinds[job.kind];
        if (!kind?.section) return;
        if (job.kind === "training.run") return open({ section: "training", runId: job.id });
        if (job.kind === "checks.evaluate") return open({ section: "checks", checksMode: "dataset" });
        if (job.kind === "training.export") {
            // An export job has no version ID until it ends: the version is found by the number of its label (Version N).
            const number = exportedVersionNumber(job.label);
            setOpening(true);
            try {
                const versions = number === null ? [] : await trainingApi.listModelVersions({ projectId });
                return open({ section: "models", versionId: versions.find(version => version.number === number)?.id });
            } catch {
                return open({ section: "models" });
            } finally {
                setOpening(false);
            }
        }
        open({ section: kind.section });
    }

    return (
        <div className="space-y-6" data-testid="overview-section">
            <section className="flex flex-wrap items-start justify-between gap-4 border-b pb-5" aria-label={t("projectAria")}>
                <div className="min-w-0">
                    <p className="max-w-prose whitespace-pre-wrap break-words text-sm" data-testid="overview-description">
                        {project.description || <span className="text-muted-foreground">{t("noDescription")}</span>}
                    </p>
                    <p className="mt-2 text-sm text-muted-foreground tabular-nums" data-testid="overview-architecture">
                        {project.baseModelId} · {project.inputSize.width} × {project.inputSize.height} px
                    </p>
                </div>
                <Button variant="outline" onClick={edit} data-testid="overview-edit"><Settings />{tc("app.projectSettings")}</Button>
            </section>
            {weightsMissing && (
                <Alert>
                    <AlertDescription>
                        <p>{t("weightsMissing", { model: project.baseModelId })}</p>
                        <Button variant="outline" size="sm" className="mt-2" onClick={openCatalog}>{t("openCatalog")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(260px,340px)]">
                <div className="min-w-0 space-y-6">
                    <section aria-labelledby="overview-data-heading" data-testid="overview-data">
                        <h2 id="overview-data-heading" className="font-medium">{t("data.title")}</h2>
                        {data.error && (
                            <Alert variant="destructive" className="mt-3" data-testid="overview-error">
                                <AlertDescription>
                                    <p>{t("data.error")}</p>
                                    <Button variant="ghost" size="sm" className="mt-1" onClick={() => void data.retry()}>{t("retry")}</Button>
                                </AlertDescription>
                            </Alert>
                        )}
                        {!state ? (
                            !data.error && <p role="status" className="mt-3 text-sm text-muted-foreground">{t("data.loading")}</p>
                        ) : (
                            <>
                                <dl className="mt-3 flex flex-wrap gap-x-8 gap-y-3 rounded-lg border bg-card p-4 text-sm">
                                    <Counter label={t("data.readyDatasets")} value={state.overview.datasets.ready} testId="overview-ready-datasets" />
                                    <Counter label={t("data.modelVersions")} value={state.overview.modelVersionCount} testId="overview-model-versions" />
                                    <Counter label={t("data.activeJobs")} value={state.total} testId="overview-active-total" />
                                </dl>
                                <div className="mt-2 flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
                                    <span data-testid="overview-dataset-states">
                                        {t("data.states", { archived: formatNumber(state.overview.datasets.archived), corrupt: formatNumber(state.overview.datasets.corrupt) })}
                                    </span>
                                    <Button size="sm" variant="link" className="h-auto p-0 text-xs" data-testid="overview-open-datasets" onClick={() => open({ section: "datasets" })}>
                                        {t("data.openDatasets")}
                                    </Button>
                                </div>
                            </>
                        )}
                    </section>
                    <section aria-labelledby="overview-jobs-heading" data-testid="overview-jobs">
                        <h2 id="overview-jobs-heading" className="font-medium">{t("jobs.title")}</h2>
                        {state && (state.jobs.length === 0 ? (
                            <p className="mt-3 text-sm text-muted-foreground" data-testid="overview-jobs-empty">{t("jobs.empty")}</p>
                        ) : (
                            <>
                                <ul className="mt-3 max-h-96 divide-y overflow-y-auto rounded-lg border bg-card" data-testid="overview-job-list">
                                    {state.jobs.map(item => (
                                        <ActiveJobRow key={item.job.id} item={item} disabled={opening} open={() => void openJob(item)} />
                                    ))}
                                </ul>
                                {state.nextCursor && (
                                    <Button className="mt-2" size="sm" variant="outline" disabled={data.loadingMore} data-testid="overview-jobs-more" onClick={() => void data.loadMore()}>
                                        {data.loadingMore ? t("jobs.loading") : t("jobs.more")}
                                    </Button>
                                )}
                            </>
                        ))}
                    </section>
                    <section aria-labelledby="overview-models-heading" data-testid="overview-models">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                            <h2 id="overview-models-heading" className="font-medium">{t("models.title")}</h2>
                            <Button variant="ghost" size="sm" data-testid="overview-all-models" onClick={() => open({ section: "models" })}>{t("models.all")}</Button>
                        </div>
                        {state && (!state.overview.latestVersions.length ? (
                            <p className="mt-3 text-sm text-muted-foreground" data-testid="overview-models-empty">{t("models.empty")}</p>
                        ) : (
                            <ul className="mt-3 divide-y rounded-lg border bg-card" data-testid="overview-model-list">
                                {state.overview.latestVersions.map(version => (
                                    <li key={version.id} className="flex flex-wrap items-center gap-3 p-3" data-testid="overview-model" data-version-id={version.id}>
                                        <div className="min-w-0 flex-1">
                                            <div className="flex flex-wrap items-center gap-2">
                                                <h3 className="text-sm font-medium">{t("models.version", { number: version.number, architecture: version.architecture })}</h3>
                                                {version.isWorking && <Badge variant="secondary" data-testid="overview-model-working">{t("models.working")}</Badge>}
                                            </div>
                                            <p className="mt-1 text-xs text-muted-foreground">{formatDate(version.createdAt)}</p>
                                            <p className="mt-1 text-xs tabular-nums" data-testid="overview-model-metrics">
                                                mAP50 {formatMetric(version.metrics.mAP50)} · mAP50–95 {formatMetric(version.metrics.mAP50_95)}
                                            </p>
                                        </div>
                                        <Button size="sm" variant="outline" data-testid="overview-model-passport" onClick={() => open({ section: "models", versionId: version.id })}>
                                            {t("models.passport")}
                                        </Button>
                                    </li>
                                ))}
                            </ul>
                        ))}
                    </section>
                </div>
                <OverviewHardware />
            </div>
        </div>
    );
}

function Counter({ label, value, testId }: { label: string; value: number; testId: string }) {
    return (
        <div>
            <dt className="text-muted-foreground">{label}</dt>
            <dd className="mt-1 text-lg font-semibold tabular-nums" data-testid={testId}>{formatNumber(value)}</dd>
        </div>
    );
}

function ActiveJobRow({ item, disabled, open }: { item: ActiveJobResponseDto; disabled: boolean; open: () => void }) {
    const { t } = useTranslation("overview");
    const percent = activeJobPercent(item);
    const known = !!activeJobKinds[item.job.kind]?.section;
    return (
        <li className="flex flex-wrap items-center gap-3 p-3" data-testid="overview-job" data-job-id={item.job.id} data-kind={item.job.kind} data-status={item.job.status}>
            <div className="min-w-0 flex-1">
                <h3 className="break-words text-sm font-medium" data-testid="overview-job-title">{activeJobTitle(item)}</h3>
                <p className="mt-1 text-xs text-muted-foreground tabular-nums" data-testid="overview-job-state">{activeJobState(item)}</p>
                <div className="mt-2 h-1 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("jobs.progressAria", { title: activeJobTitle(item) })} aria-valuemin={0}
                    aria-valuemax={100} aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                    <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"} style={percent === null ? undefined : { width: `${percent}%` }} />
                </div>
            </div>
            {known && <Button size="sm" variant="outline" disabled={disabled} data-testid="overview-job-open" onClick={open}>{t("jobs.open")}</Button>}
        </li>
    );
}
