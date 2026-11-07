import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import type { TrainingParameters } from "@/api/projects/training/request-dto/training-request-dto";
import type { TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import type { TrainingPresetResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { isActiveTrainingRun, trainingRunStateLabels } from "@/api/projects/training/training-parameters";
import { BridgeError } from "@/api/transport/errors";
import { EmptyState } from "@/components/layout/empty-state";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { SectionLayout } from "@/components/layout/section-layout";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { errorMessage, formatDate } from "@/lib/format";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useModelVersions } from "@/lib/use-model-versions";
import { useTrainingRuns } from "@/lib/use-training-runs";
import { useTrainingSetup } from "@/lib/use-training-setup";
import { TrainingRunCard } from "./training-run-card";
import { TrainingRunPassport } from "./training-run-passport";
import { TrainingSettings } from "./training-settings";
import { trainingPanelId, trainingTabId, trainingTabs, type TrainingTab } from "./training-tabs";

interface NoticeState {
    tone: "info" | "error";
    text: string;
}

/**
 * The section "Training": tabs "Settings" (the run form), "Current run" (the passport of the chosen run with charts and the log, after
 * "Done" — a short version passport), "Training history" (run cards). No polling: training.changed, job.changed, trainingPresets.changed,
 * datasets.changed. All panels stay mounted: the form survives tab switches. initialRunId — the intent of a move (training from the
 * overview): the tab "Current run" with this run; a run beyond the first page is read with getRun.
 */
export function TrainingSection({ project, initialRunId, openDatasets }: { project: ProjectResponseDto; initialRunId?: string; openDatasets: () => void }) {
    const { t } = useTranslation("training");
    const projectId = project.id;
    const datasets = useDatasetVersions(projectId);
    const runs = useTrainingRuns(projectId);
    const versions = useModelVersions(projectId);
    const setup = useTrainingSetup();
    const [tab, setTab] = useState<TrainingTab>(initialRunId ? "current" : "settings");
    const [selectedRunId, setSelectedRunId] = useState(initialRunId);
    /** The run of the intent is still being read: it may be missing from the first page of the history. */
    const [seeking, setSeeking] = useState(!!initialRunId);
    const sought = useRef(false);
    const [busy, setBusy] = useState(false);
    const [notice, setNotice] = useState<NoticeState>();
    const [hostField, setHostField] = useState<string>();

    useEffect(() => {
        if (!seeking || runs.loading || !initialRunId) return;
        if (runs.runs.some(run => run.id === initialRunId)) {
            setSeeking(false);
            return;
        }
        if (sought.current) return;
        sought.current = true;
        // A deleted run is not found: the tab shows the last run.
        trainingApi.getRun({ projectId, runId: initialRunId }).then(run => runs.apply(run), () => undefined).finally(() => setSeeking(false));
    }, [seeking, runs.loading, runs.runs, runs.apply, initialRunId, projectId]);

    const activeRuns = runs.runs.filter(run => isActiveTrainingRun(run.state));
    const current = runs.runs.find(run => run.id === selectedRunId)
        ?? activeRuns.find(run => run.state === "training" || run.state === "preparing") ?? activeRuns[0] ?? runs.runs[0];
    const selectable = [...new Map([...(current ? [current] : []), ...activeRuns].map(run => [run.id, run])).values()];
    const versionOf = (versionId: string | null) => versions.versions.find(version => version.id === versionId);
    /** A run keeps resultVersionId after its version is deleted: the version is missing from a list read after the run ended. */
    const versionDeleted = (run: TrainingRunResponseDto) => !!run.resultVersionId && versions.loaded && !versionOf(run.resultVersionId)
        && versions.listedAt > Date.parse(run.job.finishedAt ?? run.job.updatedAt);

    async function act<T>(work: () => Promise<T>): Promise<T | undefined> {
        if (busy) return undefined;
        setBusy(true);
        try {
            return await work();
        } catch (cause) {
            if (cause instanceof BridgeError && cause.details?.field) setHostField(cause.details.field);
            setNotice({ tone: "error", text: errorMessage(cause) });
            return undefined;
        } finally {
            setBusy(false);
        }
    }

    function showTab(next: TrainingTab) {
        setTab(next);
        requestAnimationFrame(() => document.getElementById(trainingTabId(next))?.focus());
    }

    function start(datasetVersionId: string, parameters: TrainingParameters, presetName: string | null) {
        setNotice(undefined);
        setHostField(undefined);
        void act(async () => {
            const run = await trainingApi.startRun({ projectId, datasetVersionId, parameters, presetName, operationId: crypto.randomUUID() });
            runs.apply(run);
            setSelectedRunId(run.id);
            showTab("current");
        });
    }

    async function savePreset(name: string, parameters: TrainingParameters): Promise<string | null> {
        setNotice(undefined);
        const saved = await act(() => trainingApi.savePreset({ name, parameters }));
        if (!saved) return null;
        await setup.refreshPresets();
        setNotice({ tone: "info", text: t("section.presetSaved", { name: saved.name }) });
        return saved.id;
    }

    async function deletePreset(preset: TrainingPresetResponseDto): Promise<boolean> {
        setNotice(undefined);
        const removed = await act(() => trainingApi.deletePreset({ presetId: preset.id }));
        if (!removed) return false;
        await setup.refreshPresets();
        setNotice({ tone: "info", text: t("section.presetDeleted", { name: preset.name }) });
        return true;
    }

    function cancel(runId: string) {
        setNotice(undefined);
        void act(() => runs.cancel(runId));
    }

    function openRun(runId: string, versionId?: string) {
        setSelectedRunId(runId);
        showTab("current");
        if (versionId) requestAnimationFrame(() => requestAnimationFrame(() => document.getElementById(`training-version-${versionId}`)?.scrollIntoView({ block: "start" })));
    }

    const loadError = runs.error ?? datasets.error ?? versions.error;
    const panel = (id: TrainingTab) => ({ value: id, id: trainingPanelId(id), "aria-labelledby": trainingTabId(id), forceMount: true as const, hidden: tab !== id });
    return (
        <SectionLayout data-testid="training-section">
            {/* Every panel stays mounted: the form survives switching, the run passport gets events while hidden. */}
            <Tabs value={tab} onValueChange={value => setTab(value as TrainingTab)} className="flex-1">
                <TabsList aria-label={t("tabs.label")}>
                    {trainingTabs.map(item => (
                        <TabsTrigger key={item.id} value={item.id} id={trainingTabId(item.id)} aria-controls={trainingPanelId(item.id)} data-testid={`training-tab-${item.id}`}
                            count={item.id === "history" ? runs.total : undefined} live={item.id === "history" && activeRuns.length > 0}>
                            {item.label}
                        </TabsTrigger>
                    ))}
                </TabsList>
                {notice && (
                    <Notice variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="training-notice" data-tone={notice.tone}
                        onDismiss={() => setNotice(undefined)}>
                        {notice.text}
                    </Notice>
                )}
                {loadError && (
                    <Notice variant="destructive" className="shrink-0"
                        action={{ label: t("section.retry"), onClick: () => { void runs.refresh(); void datasets.refresh(); void versions.refresh(); } }}>
                        {loadError}
                    </Notice>
                )}
                <TabsContent {...panel("settings")} className="flex flex-col">
                    <Pane className="flex-1">
                        <TrainingSettings
                            project={project}
                            datasets={datasets.versions}
                            datasetsLoading={datasets.loading}
                            presets={setup.presets}
                            runtime={setup.runtime}
                            gpus={setup.gpus}
                            hasActive={activeRuns.length > 0}
                            busy={busy}
                            hostField={hostField}
                            start={start}
                            savePreset={savePreset}
                            deletePreset={deletePreset}
                            openDatasets={openDatasets}
                        />
                    </Pane>
                </TabsContent>
                {/* At lg the passport and the log are two columns with a scroll each; below lg they stack in the one scroll of the tab. */}
                <TabsContent {...panel("current")} className="flex flex-col gap-4 overflow-y-auto overscroll-contain lg:overflow-hidden">
                    {selectable.length > 1 && (
                        <label className="block shrink-0 space-y-1">
                            <span className="block text-xs text-text-2">{t("section.currentRun")}</span>
                            <Select value={current?.id} onValueChange={setSelectedRunId}>
                                <SelectTrigger aria-label={t("section.currentRun")} className="w-full sm:w-96" data-testid="training-current-select"><SelectValue /></SelectTrigger>
                                <SelectContent>
                                    {selectable.map(run => (
                                        <SelectItem key={run.id} value={run.id}>{trainingRunStateLabels[run.state]} · {run.dataset.name} · {formatDate(run.job.createdAt)}</SelectItem>
                                    ))}
                                </SelectContent>
                            </Select>
                        </label>
                    )}
                    {seeking && !runs.runs.some(run => run.id === initialRunId) ? (
                        <EmptyState title={t("section.runLoading")} className={emptyClasses} data-testid="training-current-empty" />
                    ) : current ? (
                        <TrainingRunPassport
                            key={current.id}
                            projectId={projectId}
                            run={current}
                            version={versionOf(current.resultVersionId)}
                            versionDeleted={versionDeleted(current)}
                            busy={busy}
                            cancel={() => cancel(current.id)}
                            renewVersion={() => { if (current.resultVersionId) void versions.renew(current.resultVersionId); }}
                            setWorking={() => { const versionId = current.resultVersionId; if (versionId) void act(() => versions.setWorking(versionId)); }}
                        />
                    ) : (
                        <EmptyState title={runs.loading ? t("section.runLoading") : t("section.currentEmpty")} className={emptyClasses} data-testid="training-current-empty" />
                    )}
                </TabsContent>
                <TabsContent {...panel("history")} className="flex flex-col">
                    <Pane className="flex-1 space-y-3">
                        <h2 className="text-[15px] font-semibold tracking-[-0.015em]">{t("section.runs")}</h2>
                        {runs.runs.length > 0 && (
                            <ul className="space-y-3" data-testid="training-history">
                                {runs.runs.map(run => (
                                    <TrainingRunCard
                                        key={run.id}
                                        run={run}
                                        version={versionOf(run.resultVersionId)}
                                        versionDeleted={versionDeleted(run)}
                                        busy={busy}
                                        openRun={() => openRun(run.id)}
                                        openVersion={() => openRun(run.id, run.resultVersionId ?? undefined)}
                                        cancel={() => cancel(run.id)}
                                    />
                                ))}
                            </ul>
                        )}
                        {!runs.runs.length && (
                            <EmptyState title={runs.loading ? t("section.loading") : t("section.historyEmpty")} className={emptyClasses} data-testid="training-history-empty" />
                        )}
                        {runs.total > runs.runs.length && (
                            <Button variant="outline" className="w-full" disabled={runs.loadingMore} onClick={() => void runs.loadMore()}>
                                {runs.loadingMore ? t("section.loading") : t("section.moreRuns", { shown: runs.runs.length, total: runs.total })}
                            </Button>
                        )}
                    </Pane>
                </TabsContent>
            </Tabs>
        </SectionLayout>
    );
}

/** An empty tab: a dashed frame with the hint. */
const emptyClasses = "min-h-40 rounded-lg border border-dashed border-line";
