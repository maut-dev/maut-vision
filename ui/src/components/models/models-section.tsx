import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { ModelVersionPassport } from "@/components/training/model-version-passport";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { bestOf, qualityMetrics, type QualityMetric } from "@/lib/checks/check-view";
import { errorMessage } from "@/lib/format";
import { readEpochHistory } from "@/lib/training/epoch-history";
import { formatBytes } from "@/lib/training/run-view";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useModelExport, type ModelExportState } from "@/lib/use-model-export";
import { useModelVersions } from "@/lib/use-model-versions";
import { ModelVersionCard } from "./model-version-card";
import { ModelVersionDeletionDialog } from "./model-version-deletion-dialog";
import { jobErrorText } from "@/lib/errors";
import "@/i18n/sections/models";

/**
 * The section "Models": all model versions of the project (newest first) with metrics, the working version, ZIP export, deletion and the
 * passport. No polling: training.changed (versions, deleted ones too), datasets.changed (dataset names), job.changed (the export).
 */
export function ModelsSection({ project, initialVersionId, openTraining }: {
    project: ProjectResponseDto;
    initialVersionId?: string;
    openTraining: () => void;
}) {
    const { t } = useTranslation("models");
    const projectId = project.id;
    const models = useModelVersions(projectId);
    const datasets = useDatasetVersions(projectId);
    const exporter = useModelExport(projectId);
    const [passportId, setPassportId] = useState(initialVersionId);
    const [deleting, setDeleting] = useState<ModelVersionResponseDto>();
    const [busy, setBusy] = useState(false);
    const [notice, setNotice] = useState<{ tone: "info" | "error"; text: string }>();
    const versions = [...models.versions].sort((left, right) => right.number - left.number);
    const passport = versions.find(item => item.id === passportId);
    const working = versions.find(item => item.isWorking);

    useEffect(() => {
        // A deleted version does not keep the passport open.
        if (passportId && models.loaded && !passport) setPassportId(undefined);
    }, [passportId, passport, models.loaded]);

    async function act(work: () => Promise<unknown>) {
        if (busy) return;
        setBusy(true);
        setNotice(undefined);
        try {
            await work();
        } catch (cause) {
            setNotice({ tone: "error", text: errorMessage(cause) });
        } finally {
            setBusy(false);
        }
    }

    const datasetName = (version: ModelVersionResponseDto) => {
        const dataset = datasets.versions.find(item => item.id === version.dataset.versionId);
        if (!dataset) return datasets.loading ? version.dataset.name : t("section.datasetMissing");
        return dataset.archivedAt ? t("section.datasetArchived", { name: dataset.name }) : dataset.name;
    };

    const actions = (version: ModelVersionResponseDto) => ({
        setWorking: () => void act(() => models.setWorking(version.id)),
        exportZip: () => void act(() => exporter.exportVersion(version.id, version.number)),
        remove: () => setDeleting(version),
    });

    const header = (
        <>
            {notice && (
                <Alert variant={notice.tone === "error" ? "destructive" : "default"} data-testid="models-notice" data-tone={notice.tone}>
                    <AlertDescription>
                        <p>{notice.text}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setNotice(undefined)}>{t("section.hide")}</Button>
                    </AlertDescription>
                </Alert>
            )}
            {models.error && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{models.error}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => void models.refresh()}>{t("section.retry")}</Button>
                    </AlertDescription>
                </Alert>
            )}
        </>
    );

    const dialog = deleting && (
        <ModelVersionDeletionDialog
            projectId={projectId}
            versionId={deleting.id}
            number={deleting.number}
            close={() => setDeleting(undefined)}
            deleted={result => {
                setDeleting(undefined);
                if (passportId === result.versionId) setPassportId(undefined);
                setNotice({ tone: "info", text: t("section.deleted", { number: result.number }) });
                void models.refresh();
            }}
        />
    );

    if (passport) {
        return (
            <div className="space-y-4" data-testid="models-section">
                {header}
                <ExportPlate state={exporter.state} versionId={passport.id} dismiss={exporter.dismiss} />
                <VersionPassport
                    projectId={projectId}
                    version={passport}
                    busy={busy || exporter.active}
                    renew={() => void models.renew(passport.id)}
                    back={() => setPassportId(undefined)}
                    {...actions(passport)}
                />
                {dialog}
            </div>
        );
    }

    if (!models.loaded) {
        return (
            <div className="space-y-4" data-testid="models-section">
                {header}
                {!models.error && <p role="status" className="text-sm text-muted-foreground">{t("section.loading")}</p>}
            </div>
        );
    }

    if (!versions.length) {
        return (
            <div className="space-y-4" data-testid="models-section">
                {header}
                <div className="flex min-h-40 flex-col items-center justify-center gap-3 rounded-md border border-dashed p-6 text-center" data-testid="models-empty">
                    <p className="max-w-prose text-sm text-muted-foreground">
                        {t("section.empty")}
                    </p>
                    <Button variant="outline" data-testid="models-open-training" onClick={openTraining}>{t("section.toTraining")}</Button>
                </div>
            </div>
        );
    }

    const best = Object.fromEntries(qualityMetrics.map(key => [key, bestOf(versions.map(item => item.metrics[key]))])) as Record<QualityMetric, number | null>;
    return (
        <div className="space-y-4" data-testid="models-section">
            {header}
            <div className="flex flex-wrap items-end justify-between gap-3">
                <div>
                    <h2 className="font-medium">{t("section.title")}</h2>
                    <p className="mt-1 text-xs text-muted-foreground" data-testid="models-summary">
                        {working
                            ? t("section.summary", { count: versions.length, number: working.number })
                            : t("section.summaryNoWorking", { count: versions.length })}
                    </p>
                </div>
                <Button variant="outline" size="sm" onClick={openTraining}>{t("section.toTraining")}</Button>
            </div>
            <ExportPlate state={exporter.state} dismiss={exporter.dismiss} />
            <ul className="space-y-3" data-testid="models-list">
                {versions.map(version => (
                    <ModelVersionCard
                        key={version.id}
                        version={version}
                        datasetName={datasetName(version)}
                        best={best}
                        busy={busy || exporter.active}
                        openPassport={() => setPassportId(version.id)}
                        {...actions(version)}
                    />
                ))}
            </ul>
            {dialog}
        </div>
    );
}

/** Progress and result of the export: "Version N exported: model-vN.zip · 1.2 MB"; in the passport — only of its own version. */
function ExportPlate({ state, versionId, dismiss }: { state: ModelExportState | undefined; versionId?: string; dismiss: () => void }) {
    const { t } = useTranslation("models");
    if (!state || (versionId && state.versionId !== versionId)) return null;
    const { job, result } = state;
    const running = job.status === "queued" || job.status === "running";
    return (
        <div className="space-y-2 rounded-md border p-3 text-sm" role="status" data-testid="model-export" data-status={job.status}>
            {running && (
                <>
                    <p className="text-muted-foreground">{t("export.running", { number: state.versionNumber, fileName: state.fileName })}</p>
                    <div className="h-1.5 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("export.progress")} aria-valuemin={0} aria-valuemax={100}
                        aria-valuenow={job.progress ?? undefined}>
                        <div className={job.progress === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                            style={job.progress === null ? undefined : { width: `${job.progress}%` }} />
                    </div>
                </>
            )}
            {job.status === "succeeded" && (
                <p data-testid="model-export-result">
                    {result
                        ? t("export.doneSize", { number: result.versionNumber, fileName: result.fileName, size: formatBytes(result.bytes) })
                        : t("export.done", { number: state.versionNumber, fileName: state.fileName })}
                </p>
            )}
            {!running && job.status !== "succeeded" && (
                <p className="text-destructive" data-testid="model-export-error">
                    {job.status === "cancelled" ? t("export.cancelled") : (job.error ? jobErrorText(job.error) : t("export.failed"))}
                </p>
            )}
            {!running && <Button variant="ghost" size="xs" onClick={dismiss}>{t("section.hide")}</Button>}
        </div>
    );
}

/** The version passport in "Models": the epoch history of the run for "best …" is read by pages (readEpochHistory). */
function VersionPassport({ projectId, version, busy, renew, back, setWorking, exportZip, remove }: {
    projectId: string;
    version: ModelVersionResponseDto;
    busy: boolean;
    renew: () => void;
    back: () => void;
    setWorking: () => void;
    exportZip: () => void;
    remove: () => void;
}) {
    const [epochs, setEpochs] = useState<readonly TrainingEpochDto[]>([]);
    const runId = version.runId;

    useEffect(() => {
        let active = true;
        // Without the epoch history the passport shows the final best.pt.
        readEpochHistory(projectId, runId, () => active).then(items => { if (active && items) setEpochs(items); }, () => undefined);
        return () => { active = false; };
    }, [projectId, runId]);

    return <ModelVersionPassport version={version} epochs={epochs} busy={busy} renew={renew} setWorking={setWorking} exportZip={exportZip} remove={remove} back={back} />;
}
