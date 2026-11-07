import { useState } from "react";
import { Archive, ArchiveRestore, Download, Pencil, ShieldCheck, Trash2 } from "lucide-react";
import { Trans, useTranslation } from "react-i18next";
import { datasetTexts, integrityLabels } from "@/api/projects/datasets/dataset-rules";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Pane } from "@/components/layout/pane";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { Toolbar } from "@/components/layout/toolbar";
import { Button } from "@/components/ui/button";
import { classOrderText, count } from "@/lib/datasets/dataset-view";
import { formatDate } from "@/lib/format";
import { DatasetSamples } from "./dataset-samples";

type DetailTab = "passport" | "samples";

/**
 * The chosen version: a compact header with the name and the actions "Check integrity", "ZIP export", "Edit", "Archive" / "Restore",
 * "Delete dataset", and two tabs. "Details" is the passport: splits, integrity, build, class order, composition, a note on immutability;
 * it stays mounted. "Samples" is the virtual grid of one split, the only scroll of the tab. An archived version cannot be checked,
 * exported or edited; a corrupt one cannot be exported. The chosen tab survives choosing another version.
 */
export function DatasetDetails({ projectId, version, busy, verify, exportZip, edit, toggleArchive, remove }: {
    projectId: string;
    version: DatasetVersionResponseDto;
    busy: boolean;
    verify: () => void;
    exportZip: () => void;
    edit: () => void;
    toggleArchive: () => void;
    remove: () => void;
}) {
    const { t } = useTranslation("datasets");
    const [tab, setTab] = useState<DetailTab>("passport");
    const archived = !!version.archivedAt;
    const corrupt = version.integrityState === "corrupt";
    const integrityValues = {
        state: integrityLabels[version.integrityState],
        date: version.verifiedAt ? formatDate(version.verifiedAt) : "",
        status: archived ? datasetTexts.archived : t("details.active"),
    };
    const { composition } = version;
    return (
        <div className="flex min-h-0 min-w-0 flex-1 flex-col gap-2" data-testid="dataset-details" data-dataset-details-id={version.id} data-integrity={version.integrityState}>
            <div className="flex min-w-0 shrink-0 flex-wrap items-center gap-x-3 gap-y-2">
                <h2 className="min-w-0 font-medium break-words" data-testid="dataset-title">{version.name}</h2>
                <Toolbar className="ml-auto gap-1">
                    {/* Below lg the actions keep only their icons, so the header stays one row and the tabs keep their height. */}
                    <Button size="sm" variant="outline" aria-label={t("details.verify")} title={t("details.verify")} disabled={busy || archived} data-testid="dataset-verify" onClick={verify}>
                        <ShieldCheck /><span className="hidden lg:inline">{t("details.verify")}</span>
                    </Button>
                    <Button size="sm" aria-label={t("details.export")} title={t("details.export")} disabled={busy || archived || corrupt} data-testid="dataset-export" onClick={exportZip}>
                        <Download /><span className="hidden lg:inline">{t("details.export")}</span>
                    </Button>
                    <Button size="sm" variant="ghost" aria-label={t("details.edit")} title={t("details.edit")} disabled={busy || archived} data-testid="dataset-edit" onClick={edit}>
                        <Pencil /><span className="hidden lg:inline">{t("details.edit")}</span>
                    </Button>
                    <Button size="sm" variant="ghost" aria-label={archived ? t("details.restore") : t("details.archive")} title={archived ? t("details.restore") : t("details.archive")}
                        disabled={busy} data-testid="dataset-archive" onClick={toggleArchive}>
                        {archived ? <ArchiveRestore /> : <Archive />}<span className="hidden lg:inline">{archived ? t("details.restore") : t("details.archive")}</span>
                    </Button>
                    <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" aria-label={t("details.delete")} title={t("details.delete")} disabled={busy}
                        data-testid="dataset-delete" onClick={remove}>
                        <Trash2 /><span className="hidden lg:inline">{t("details.delete")}</span>
                    </Button>
                </Toolbar>
            </div>
            <Tabs value={tab} onValueChange={value => setTab(value as DetailTab)} className="flex-1 gap-2">
                <TabsList aria-label={t("details.tabs")}>
                    <TabsTrigger value="passport" data-testid="dataset-detail-tab-passport">{t("details.passportTab")}</TabsTrigger>
                    <TabsTrigger value="samples" count={version.counts.total} data-testid="dataset-detail-tab-samples">{t("details.samplesTab")}</TabsTrigger>
                </TabsList>
                {/* Mounted while hidden: the passport is short and its lines are read by the tests of the section on any tab. */}
                <TabsContent value="passport" forceMount hidden={tab !== "passport"} className="flex flex-col">
                    <Pane surface className="flex-1 space-y-2 p-4 text-sm text-text-2" data-testid="dataset-passport">
                        {version.description && <p className="whitespace-pre-wrap break-words text-text-1">{version.description}</p>}
                        <p data-testid="dataset-counts">
                            {t("details.counts", { train: count(version.counts.train), val: count(version.counts.val), test: count(version.counts.test) })}
                        </p>
                        <p data-testid="dataset-integrity">
                            <Trans t={t} i18nKey={version.verifiedAt ? "details.integrityDate" : "details.integrity"} values={integrityValues}
                                components={{ state: <span className={corrupt ? "text-destructive" : undefined} /> }} />
                        </p>
                        <p data-testid="dataset-built">
                            {t("details.built", { date: formatDate(version.createdAt), groups: version.specification.groupIds.length, seed: version.specification.seed })}
                        </p>
                        <p data-testid="dataset-class-order">{t("details.classOrder", { order: classOrderText(version) })}</p>
                        <p>
                            {t("details.frameSize", { width: version.width, height: version.height })}
                            {!version.inputSizeMatches && <span className="text-warn" data-testid="dataset-details-size-mismatch"> · {datasetTexts.sizeMismatch}</span>}
                        </p>
                        <section className="space-y-1 pt-2 text-xs" aria-label={t("details.composition")} data-testid="dataset-composition">
                            <h3 className="text-sm font-medium text-text-1">{t("details.composition")}</h3>
                            <p>
                                {t("details.frames", { withoutBoxes: count(composition.withoutBoxes), manual: count(composition.fromManual), generated: count(composition.fromGenerated) })}
                            </p>
                            <p>{t("details.boxes", { boxes: count(composition.boxes), negatives: count(composition.negatives) })}</p>
                            <p className="break-words">{t("details.boxesPerClass", { list: composition.boxesPerClass.map(item => `${item.index}: ${item.name} — ${count(item.boxes)}`).join(", ") || "—" })}</p>
                            <p className="break-words">{t("details.groups", { list: composition.groups.map(item => `${item.name} — ${count(item.images)}`).join(", ") || "—" })}</p>
                        </section>
                        <p className="pt-2">{t("details.immutable")}</p>
                        {archived && <p data-testid="dataset-archived-note">{t("details.archivedNote")}</p>}
                        {corrupt && <p className="text-destructive" data-testid="dataset-corrupt-note">{datasetTexts.integrityHelp}</p>}
                    </Pane>
                </TabsContent>
                <TabsContent value="samples" className="flex flex-col">
                    <DatasetSamples key={version.id} projectId={projectId} version={version} />
                </TabsContent>
            </Tabs>
        </div>
    );
}
