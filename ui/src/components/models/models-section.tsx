import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { Progress } from "@/components/layout/progress";
import { SectionLayout } from "@/components/layout/section-layout";
import { Toolbar } from "@/components/layout/toolbar";
import { ModelVersionPassport } from "@/components/training/model-version-passport";
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
 * passport. No polling: training.changed (versions, deleted ones too), datasets.changed (dataset names), job.changed (the export). The section
 * fits the window: the header with notices and the export stays, the list or the passport below it is the one scroll.
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

    const notices = (
        <>
            {notice && (
                <Notice variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="models-notice" data-tone={notice.tone}
                    onDismiss={() => setNotice(undefined)}>
                    {notice.text}
                </Notice>
            )}
            {models.error && (
                <Notice variant="destructive" className="shrink-0" action={{ label: t("section.retry"), onClick: () => void models.refresh() }}>
                    {models.error}
                </Notice>
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
            <SectionLayout data-testid="models-section">
                <Toolbar className="shrink-0">
                    <Button variant="ghost" size="sm" data-testid="model-version-back" onClick={() => setPassportId(undefined)}>{t("section.back")}</Button>
                </Toolbar>
                {notices}
                <ExportPlate state={exporter.state} versionId={passport.id} dismiss={exporter.dismiss} />
                <Pane className="flex-1" role="region" aria-label={t("section.passport", { number: passport.number })}>
                    <VersionPassport
                        projectId={projectId}
                        version={passport}
                        busy={busy || exporter.active}
                        renew={() => void models.renew(passport.id)}
                        {...actions(passport)}
                    />
                </Pane>
                {dialog}
            </SectionLayout>
        );
    }

    if (!models.loaded) {
        return (
            <SectionLayout data-testid="models-section">
                {notices}
                {!models.error && <p role="status" className="text-sm text-text-2">{t("section.loading")}</p>}
            </SectionLayout>
        );
    }

    if (!versions.length) {
        return (
            <SectionLayout data-testid="models-section">
                {notices}
                <EmptyState title={t("section.empty")} className="min-h-40 shrink-0 rounded-lg border border-dashed border-line" data-testid="models-empty"
                    action={<Button variant="outline" data-testid="models-open-training" onClick={openTraining}>{t("section.toTraining")}</Button>} />
            </SectionLayout>
        );
    }

    const best = Object.fromEntries(qualityMetrics.map(key => [key, bestOf(versions.map(item => item.metrics[key]))])) as Record<QualityMetric, number | null>;
    return (
        <SectionLayout data-testid="models-section">
            <div className="flex shrink-0 flex-wrap items-end justify-between gap-3">
                <div className="min-w-0">
                    <h2 className="text-[15px] font-semibold tracking-[-0.015em]">{t("section.title")}</h2>
                    <p className="mt-1 max-w-prose text-xs text-text-2" data-testid="models-summary">
                        {working
                            ? t("section.summary", { count: versions.length, number: working.number })
                            : t("section.summaryNoWorking", { count: versions.length })}
                    </p>
                </div>
                <Button variant="outline" size="sm" onClick={openTraining}>{t("section.toTraining")}</Button>
            </div>
            {notices}
            <ExportPlate state={exporter.state} dismiss={exporter.dismiss} />
            <Pane className="flex-1" role="region" aria-label={t("section.title")}>
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
            </Pane>
            {dialog}
        </SectionLayout>
    );
}

/** Progress and result of the export: "Version N exported: model-vN.zip · 1.2 MB"; in the passport — only of its own version. */
function ExportPlate({ state, versionId, dismiss }: { state: ModelExportState | undefined; versionId?: string; dismiss: () => void }) {
    const { t } = useTranslation("models");
    if (!state || (versionId && state.versionId !== versionId)) return null;
    const { job, result } = state;
    const running = job.status === "queued" || job.status === "running";
    return (
        <div className="surface shrink-0 space-y-2 p-3 text-sm" role="status" data-testid="model-export" data-status={job.status}>
            {running && (
                <>
                    <p className="text-text-2">{t("export.running", { number: state.versionNumber, fileName: state.fileName })}</p>
                    <Progress value={job.progress} live={job.status === "running"} aria-label={t("export.progress")} />
                </>
            )}
            {job.status === "succeeded" && (
                <p className="text-ok" data-testid="model-export-result">
                    {result
                        ? t("export.doneSize", { number: result.versionNumber, fileName: result.fileName, size: formatBytes(result.bytes) })
                        : t("export.done", { number: state.versionNumber, fileName: state.fileName })}
                </p>
            )}
            {!running && job.status !== "succeeded" && (
                <p className="text-danger" data-testid="model-export-error">
                    {job.status === "cancelled" ? t("export.cancelled") : (job.error ? jobErrorText(job.error) : t("export.failed"))}
                </p>
            )}
            {!running && <Button variant="ghost" size="xs" onClick={dismiss}>{t("section.hide")}</Button>}
        </div>
    );
}

/** The version passport in "Models": the epoch history of the run for "best …" is read by pages (readEpochHistory). */
function VersionPassport({ projectId, version, busy, renew, setWorking, exportZip, remove }: {
    projectId: string;
    version: ModelVersionResponseDto;
    busy: boolean;
    renew: () => void;
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

    return <ModelVersionPassport version={version} epochs={epochs} busy={busy} renew={renew} setWorking={setWorking} exportZip={exportZip} remove={remove} />;
}
