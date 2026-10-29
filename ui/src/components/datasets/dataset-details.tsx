import { Archive, ArchiveRestore, Download, Pencil, ShieldCheck, Trash2 } from "lucide-react";
import { Trans, useTranslation } from "react-i18next";
import { datasetTexts, integrityLabels } from "@/api/projects/datasets/dataset-rules";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Button } from "@/components/ui/button";
import { classOrderText, count } from "@/lib/datasets/dataset-view";
import { formatDate } from "@/lib/format";
import { DatasetSamples } from "./dataset-samples";

/**
 * The passport of the chosen version: splits, integrity, build, class order, composition, a note on immutability; actions "Check
 * integrity", "ZIP export", "Edit", "Archive" / "Restore", "Delete dataset". An archived version cannot be checked, exported or
 * edited; a corrupt one cannot be exported. Below — samples by split.
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
    const archived = !!version.archivedAt;
    const corrupt = version.integrityState === "corrupt";
    const integrityValues = {
        state: integrityLabels[version.integrityState],
        date: version.verifiedAt ? formatDate(version.verifiedAt) : "",
        status: archived ? datasetTexts.archived : t("details.active"),
    };
    const { composition } = version;
    return (
        <div className="space-y-4" data-testid="dataset-details" data-dataset-details-id={version.id} data-integrity={version.integrityState}>
            <div className="space-y-2 text-sm text-muted-foreground" data-testid="dataset-passport">
                {version.description && <p className="whitespace-pre-wrap break-words text-foreground">{version.description}</p>}
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
                    {!version.inputSizeMatches && <span className="text-amber-700 dark:text-amber-400" data-testid="dataset-details-size-mismatch"> · {datasetTexts.sizeMismatch}</span>}
                </p>
                <details className="rounded-md border px-3 py-2" data-testid="dataset-composition">
                    <summary className="cursor-pointer text-foreground">{t("details.composition")}</summary>
                    <div className="mt-2 space-y-1 text-xs">
                        <p>
                            {t("details.frames", { withoutBoxes: count(composition.withoutBoxes), manual: count(composition.fromManual), generated: count(composition.fromGenerated) })}
                        </p>
                        <p>{t("details.boxes", { boxes: count(composition.boxes), negatives: count(composition.negatives) })}</p>
                        <p>{t("details.boxesPerClass", { list: composition.boxesPerClass.map(item => `${item.index}: ${item.name} — ${count(item.boxes)}`).join(", ") || "—" })}</p>
                        <p className="break-words">{t("details.groups", { list: composition.groups.map(item => `${item.name} — ${count(item.images)}`).join(", ") || "—" })}</p>
                    </div>
                </details>
                <p>{t("details.immutable")}</p>
                {archived && <p data-testid="dataset-archived-note">{t("details.archivedNote")}</p>}
                {corrupt && <p className="text-destructive" data-testid="dataset-corrupt-note">{datasetTexts.integrityHelp}</p>}
            </div>
            <div className="flex flex-wrap gap-2">
                <Button variant="outline" disabled={busy || archived} data-testid="dataset-verify" onClick={verify}><ShieldCheck />{t("details.verify")}</Button>
                <Button disabled={busy || archived || corrupt} data-testid="dataset-export" onClick={exportZip}><Download />{t("details.export")}</Button>
                <Button variant="outline" disabled={busy || archived} data-testid="dataset-edit" onClick={edit}><Pencil />{t("details.edit")}</Button>
                <Button variant="ghost" disabled={busy} data-testid="dataset-archive" onClick={toggleArchive}>
                    {archived ? <><ArchiveRestore />{t("details.restore")}</> : <><Archive />{t("details.archive")}</>}
                </Button>
                <Button variant="destructive" size="sm" className="self-center" disabled={busy} data-testid="dataset-delete" onClick={remove}><Trash2 />{t("details.delete")}</Button>
            </div>
            <DatasetSamples key={version.id} projectId={projectId} version={version} />
        </div>
    );
}
