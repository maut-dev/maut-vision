import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import { specificationFieldError, type DatasetSpecificationField } from "@/api/projects/datasets/dataset-rules";
import type { DatasetOperationKind } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import type { DatasetOperationResponseDto } from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import type { DatasetPlanResponseDto, DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { EmptyState } from "@/components/layout/empty-state";
import { MasterDetail } from "@/components/layout/master-detail";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { SectionLayout } from "@/components/layout/section-layout";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { emptyDatasetForm, formFromSpecification, specificationKey, toSpecification, type DatasetFormValues } from "@/lib/datasets/dataset-form";
import { isOperationActive, matchesSearch, operationTitles } from "@/lib/datasets/dataset-view";
import { errorMessage, formatNumber } from "@/lib/format";
import { useCatalog } from "@/lib/use-catalog";
import { operationPageLimit, useDatasetOperations, type DatasetOperationsState } from "@/lib/use-dataset-operations";
import { useDatasetVersions } from "@/lib/use-dataset-versions";
import { useFocusAfterRender } from "@/lib/use-focus-after-render";
import { useGroupReviewCounts } from "@/lib/use-group-review-counts";
import { useImageGroups } from "@/lib/use-image-groups";
import { DatasetBuildForm, type DatasetFieldError } from "./dataset-build-form";
import { DatasetDetails } from "./dataset-details";
import { DatasetDeletionDialog, DatasetEditDialog, DatasetOperationDeletionDialog } from "./dataset-dialogs";
import { DatasetList, versionButtonId } from "./dataset-list";
import { DatasetOperationCard } from "./dataset-operation-card";
import { DatasetPlanCard } from "./dataset-plan-card";
import { datasetTabs, panelId, tabId, type DatasetTab } from "./dataset-tabs";

interface NoticeState {
    tone: "info" | "error";
    text: string;
}

const fields: readonly DatasetSpecificationField[] = ["name", "description", "groupIds", "classIds", "variants", "roles", "ratios", "seed"];

/**
 * The section "Datasets": tabs "Datasets" (the list with search and archive, the chosen version with "Details" and "Samples"), "Build"
 * (the form, the plan, the build history), "Checks" and "Exports" (operation cards); a tab counts its items and shows a live dot while
 * an operation runs. No polling: datasets.changed, job.changed, images.changed, catalog.changed. All tabs stay mounted: the form and the
 * plan survive switching, cards get events while hidden.
 */
export function DatasetsSection({ project }: { project: ProjectResponseDto }) {
    const { t } = useTranslation("datasets");
    const projectId = project.id;
    const catalog = useCatalog(projectId);
    const groups = useImageGroups(projectId);
    const versions = useDatasetVersions(projectId);
    const builds = useDatasetOperations(projectId, "build");
    const verifies = useDatasetOperations(projectId, "verify");
    const exports = useDatasetOperations(projectId, "export");
    const operations: Record<DatasetOperationKind, DatasetOperationsState> = { build: builds, verify: verifies, export: exports };
    const [tab, setTab] = useState<DatasetTab>("datasets");
    const reviewCount = useGroupReviewCounts(projectId, groups.groups, tab === "build");
    const [selectedId, setSelectedId] = useState<string>();
    const [search, setSearch] = useState("");
    const [showArchived, setShowArchived] = useState(false);
    const [form, setForm] = useState<DatasetFormValues>(emptyDatasetForm);
    const [fieldError, setFieldError] = useState<DatasetFieldError>();
    const [repeatNotice, setRepeatNotice] = useState(false);
    /** The plan and the key of the specification it was requested with: another form specification makes the plan stale. */
    const [plan, setPlan] = useState<DatasetPlanResponseDto>();
    const [planKey, setPlanKey] = useState<string>();
    const [action, setAction] = useState<"plan" | "build" | "other">();
    const [notice, setNotice] = useState<NoticeState>();
    const [editing, setEditing] = useState<DatasetVersionResponseDto>();
    const [editError, setEditError] = useState<string>();
    const [deleting, setDeleting] = useState<DatasetVersionResponseDto>();
    const [removingOperation, setRemovingOperation] = useState<{ operation: DatasetOperationResponseDto; name: string }>();
    const nameRef = useRef<HTMLInputElement>(null);
    const busy = action !== undefined;

    const specification = toSpecification(form, catalog.workspace);
    const invalidField = specificationFieldError(specification);
    const planStale = !!plan && planKey !== specificationKey(specification);
    const visible = versions.versions.filter(version => (showArchived || !version.archivedAt) && matchesSearch(version, search));
    const selected = visible.find(version => version.id === selectedId);
    const versionOf = (id: string) => versions.versions.find(version => version.id === id);

    useEffect(() => {
        if (!versions.loading && !visible.some(version => version.id === selectedId)) setSelectedId(visible[0]?.id);
    }, [versions.loading, visible, selectedId]);

    const focusAfterRender = useFocusAfterRender(busy);

    /** Focus after the render that enables the target: the buttons of the details are disabled while busy. */
    function focusLater(target: string | (() => HTMLElement | null | undefined)) {
        focusAfterRender(typeof target === "string" ? () => document.getElementById(target) : target);
    }

    function updateForm(update: (current: DatasetFormValues) => DatasetFormValues) {
        setForm(update);
        setFieldError(undefined);
    }

    /** The text of a host error; a wrong specification field is highlighted in the form. */
    function describe(cause: unknown): string {
        const message = errorMessage(cause);
        if (cause instanceof BridgeError) {
            const field = cause.details?.field as DatasetSpecificationField | undefined;
            if (field && fields.includes(field)) setFieldError({ field, message });
            if (cause.code === "datasets.revision_conflict") void versions.refresh();
            if (cause.code === "datasets.plan_stale") setPlan(undefined);
        }
        return message;
    }

    async function run(kind: "plan" | "build" | "other", work: () => Promise<void>): Promise<boolean> {
        if (busy) return false;
        setAction(kind);
        try {
            await work();
            return true;
        } catch (cause) {
            setNotice({ tone: "error", text: describe(cause) });
            return false;
        } finally {
            setAction(undefined);
        }
    }

    function showTab(next: DatasetTab) {
        setTab(next);
        focusLater(tabId(next));
    }

    function requestPlan() {
        if (invalidField) return;
        setNotice(undefined);
        setFieldError(undefined);
        void run("plan", async () => {
            const key = specificationKey(specification);
            setPlan(await datasetsApi.plan({ projectId, specification, operationId: crypto.randomUUID() }));
            setPlanKey(key);
            setRepeatNotice(false);
        });
    }

    function build() {
        if (!plan) return;
        setNotice(undefined);
        void run("build", async () => {
            const operation = await datasetsApi.startBuild({ projectId, planId: plan.id, planHash: plan.hash, operationId: crypto.randomUUID() });
            builds.apply(operation);
            setPlan(undefined);
            setNotice({ tone: "info", text: t("section.buildQueued", { name: operation.versionName }) });
        });
    }

    function verify(version: DatasetVersionResponseDto) {
        setNotice(undefined);
        void run("other", async () => {
            verifies.apply(await datasetsApi.startVerify({ projectId, versionId: version.id, operationId: crypto.randomUUID() }));
            showTab("verify");
        });
    }

    function exportZip(version: DatasetVersionResponseDto) {
        setNotice(undefined);
        void run("other", async () => {
            const destination = await datasetsApi.selectExportDestination({ projectId, versionId: version.id });
            if (!destination.token) return;
            exports.apply(await datasetsApi.startExport({ projectId, versionId: version.id, destinationToken: destination.token, operationId: crypto.randomUUID() }));
            showTab("export");
        });
    }

    function toggleArchive(version: DatasetVersionResponseDto) {
        setNotice(undefined);
        void run("other", async () => {
            const saved = await datasetsApi.setArchived({ projectId, versionId: version.id, archived: !version.archivedAt, revision: version.revision });
            versions.apply(saved);
            setNotice({ tone: "info", text: saved.archivedAt ? t("section.archived", { name: saved.name }) : t("section.restored", { name: saved.name }) });
            focusLater(() => document.querySelector<HTMLElement>(saved.archivedAt && !showArchived ? '[data-testid="dataset-search"]' : '[data-testid="dataset-archive"]'));
        });
    }

    async function saveEdit(values: { name: string; description: string }) {
        if (!editing) return;
        setEditError(undefined);
        setAction("other");
        try {
            versions.apply(await datasetsApi.update({ projectId, versionId: editing.id, ...values, revision: editing.revision }));
            setEditing(undefined);
        } catch (cause) {
            setEditError(errorMessage(cause));
            if (cause instanceof BridgeError && cause.code === "datasets.revision_conflict") void versions.refresh();
        } finally {
            setAction(undefined);
        }
    }

    function repeat(operation: DatasetOperationResponseDto) {
        if (operation.kind === "build") {
            if (!operation.specification) return;
            setNotice(undefined);
            setFieldError(undefined);
            setPlan(undefined);
            setForm(formFromSpecification(operation.specification));
            setRepeatNotice(true);
            setTab("build");
            focusLater(() => nameRef.current);
            return;
        }
        const version = versionOf(operation.versionId);
        if (!version) return;
        if (operation.kind === "verify") verify(version);
        else exportZip(version);
    }

    function cancel(operation: DatasetOperationResponseDto) {
        void operations[operation.kind].cancel(operation).catch(cause => setNotice({ tone: "error", text: errorMessage(cause) }));
    }

    function openDataset(versionId: string) {
        const version = versionOf(versionId);
        if (!version) return;
        if (version.archivedAt) setShowArchived(true);
        if (!matchesSearch(version, search)) setSearch("");
        setSelectedId(versionId);
        setTab("datasets");
        focusLater(versionButtonId(versionId));
    }

    const loadError = catalog.error ?? groups.error ?? versions.error;
    const counts: Record<DatasetTab, number> = {
        datasets: versions.versions.filter(version => !version.archivedAt).length,
        build: builds.total,
        verify: verifies.total,
        export: exports.total,
    };
    const live = (tab: DatasetTab) => tab !== "datasets" && operations[tab].operations.some(operation => isOperationActive(operation.job));
    const panel = (id: DatasetTab) => ({ value: id, id: panelId(id), "aria-labelledby": tabId(id), forceMount: true as const, hidden: tab !== id });
    return (
        <SectionLayout data-testid="datasets-section">
            {/* Every panel stays mounted: the form and the plan survive switching, operation cards get events while hidden. */}
            <Tabs value={tab} onValueChange={value => setTab(value as DatasetTab)} className="flex-1">
                <TabsList aria-label={t("tabs.label")}>
                    {datasetTabs.map(item => (
                        <TabsTrigger key={item.id} value={item.id} id={tabId(item.id)} aria-controls={panelId(item.id)} data-testid={`datasets-tab-${item.id}`}
                            count={counts[item.id]} live={live(item.id)}>
                            {item.label}
                        </TabsTrigger>
                    ))}
                </TabsList>
                {notice && (
                    <Notice variant={notice.tone === "error" ? "destructive" : "default"} className="shrink-0" data-testid="datasets-notice" onDismiss={() => setNotice(undefined)}>
                        {notice.text}
                    </Notice>
                )}
                {loadError && (
                    <Notice variant="destructive" className="shrink-0"
                        action={{ label: t("section.retry"), onClick: () => { void catalog.refresh(); void groups.refresh(); void versions.refresh(); } }}>
                        {loadError}
                    </Notice>
                )}
                <TabsContent {...panel("datasets")} className="flex flex-col">
                    <MasterDetail className="grid-cols-[minmax(0,11rem)_minmax(0,1fr)] lg:grid-cols-[minmax(14rem,0.6fr)_minmax(0,1.7fr)]">
                        <DatasetList versions={versions.versions} loading={versions.loading} selectedId={selected?.id} select={setSelectedId}
                            search={search} setSearch={setSearch} showArchived={showArchived} setShowArchived={setShowArchived} />
                        <section className="flex min-h-0 min-w-0 flex-col" aria-label={t("section.selected")}>
                            {selected ? (
                                <DatasetDetails
                                    projectId={projectId}
                                    version={selected}
                                    busy={busy}
                                    verify={() => verify(selected)}
                                    exportZip={() => exportZip(selected)}
                                    edit={() => { setEditError(undefined); setEditing(selected); }}
                                    toggleArchive={() => toggleArchive(selected)}
                                    remove={() => setDeleting(selected)}
                                />
                            ) : (
                                <EmptyState title={t("section.choose")} description={t("section.chooseHint")} className="flex-1 rounded-lg border border-dashed border-line" />
                            )}
                        </section>
                    </MasterDetail>
                </TabsContent>
                {/* At lg the form and the plan with the build history are two columns with a scroll each; below lg they stack in one scroll. */}
                <TabsContent {...panel("build")}
                    className="flex flex-col gap-4 overflow-y-auto overscroll-contain lg:grid lg:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)] lg:grid-rows-[minmax(0,1fr)] lg:overflow-visible">
                    <DatasetBuildForm
                        values={form}
                        setValues={updateForm}
                        groups={groups.groups}
                        reviewCount={reviewCount}
                        workspace={catalog.workspace}
                        invalidField={invalidField}
                        fieldError={fieldError}
                        busy={busy}
                        planning={action === "plan"}
                        plan={requestPlan}
                        nameRef={nameRef}
                        repeatNotice={repeatNotice}
                    />
                    <div className="flex min-w-0 flex-col gap-4 lg:min-h-0 lg:overflow-y-auto lg:overscroll-contain" role="region" aria-label={t("section.buildResults")}
                        data-testid="dataset-build-results">
                        {plan && <DatasetPlanCard plan={plan} stale={planStale} busy={busy} building={action === "build"} build={build} />}
                        <OperationList kind="build" state={builds} versionOf={versionOf} busy={busy} cancel={cancel} repeat={repeat}
                            remove={(operation, name) => setRemovingOperation({ operation, name })} openDataset={openDataset} />
                    </div>
                </TabsContent>
                {(["verify", "export"] as const).map(kind => (
                    <TabsContent key={kind} {...panel(kind)} className="flex flex-col">
                        <Pane className="flex-1">
                            <OperationList kind={kind} state={operations[kind]} versionOf={versionOf} busy={busy} cancel={cancel} repeat={repeat}
                                remove={(operation, name) => setRemovingOperation({ operation, name })} openDataset={openDataset} />
                        </Pane>
                    </TabsContent>
                ))}
            </Tabs>
            {editing && <DatasetEditDialog version={editing} busy={busy} error={editError} close={() => setEditing(undefined)} save={values => void saveEdit(values)} />}
            {deleting && (
                <DatasetDeletionDialog
                    projectId={projectId}
                    version={deleting}
                    close={() => setDeleting(undefined)}
                    deleted={result => {
                        setDeleting(undefined);
                        versions.remove(result.versionId);
                        for (const state of Object.values(operations)) {
                            for (const operation of state.operations) if (operation.versionId === result.versionId) state.remove(operation.job.id);
                        }
                        setNotice({ tone: "info", text: result.cleanupPending ? t("section.deletedPending") : t("section.deleted") });
                        focusLater(() => document.querySelector<HTMLElement>('[data-testid="dataset-search"]'));
                    }}
                />
            )}
            {removingOperation && (
                <DatasetOperationDeletionDialog
                    projectId={projectId}
                    operationId={removingOperation.operation.job.id}
                    name={removingOperation.name}
                    close={() => setRemovingOperation(undefined)}
                    deleted={operationId => {
                        operations[removingOperation.operation.kind].remove(operationId);
                        setRemovingOperation(undefined);
                    }}
                />
            )}
        </SectionLayout>
    );
}

/** The cards of one operation kind; the history has no scroll of its own: the owner puts it into a Pane or a scrolling column. */
function OperationList({ kind, state, versionOf, busy, cancel, repeat, remove, openDataset }: {
    kind: DatasetOperationKind;
    state: DatasetOperationsState;
    versionOf: (id: string) => DatasetVersionResponseDto | undefined;
    busy: boolean;
    cancel: (operation: DatasetOperationResponseDto) => void;
    repeat: (operation: DatasetOperationResponseDto) => void;
    remove: (operation: DatasetOperationResponseDto, name: string) => void;
    openDataset: (versionId: string) => void;
}) {
    const { t } = useTranslation("datasets");
    const label = t(`section.operations.${kind}`);
    return (
        <section className="min-w-0 space-y-3" aria-label={label} data-testid={`dataset-operations-${kind}`}>
            {kind === "build" && <h2 className="text-[15px] font-semibold tracking-[-0.015em]">{label}</h2>}
            {state.error && <Notice variant="destructive" action={{ label: t("section.retry"), onClick: () => void state.refresh() }}>{state.error}</Notice>}
            {state.operations.map(operation => {
                const version = versionOf(operation.versionId);
                const name = version?.name ?? operation.versionName;
                return (
                    <DatasetOperationCard
                        key={operation.job.id}
                        operation={operation}
                        version={version}
                        busy={busy}
                        cancel={() => cancel(operation)}
                        repeat={() => repeat(operation)}
                        remove={() => remove(operation, `${operationTitles[kind]} · ${name}`)}
                        openDataset={() => openDataset(operation.versionId)}
                    />
                );
            })}
            {state.total > state.operations.length && (
                <p className="text-xs text-text-2">{t("section.lastOperations", { shown: Math.max(operationPageLimit, state.operations.length), total: formatNumber(state.total) })}</p>
            )}
            {state.loading && !state.operations.length && <p role="status" className="text-sm text-text-2">{t("section.loading")}</p>}
            {!state.loading && !state.error && !state.operations.length && (
                <EmptyState title={t(`section.empty.${kind}`)} className="rounded-lg border border-dashed border-line" data-testid={`dataset-operations-${kind}-empty`} />
            )}
        </section>
    );
}
