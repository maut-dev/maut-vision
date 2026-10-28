import { Archive, ArchiveRestore, Download, Pencil, ShieldCheck, Trash2 } from "lucide-react";
import { datasetTexts, integrityLabels } from "@/api/projects/datasets/dataset-rules";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Button } from "@/components/ui/button";
import { classOrderText, count } from "@/lib/datasets/dataset-view";
import { formatDate } from "@/lib/format";
import { DatasetSamples } from "./dataset-samples";

/**
 * Паспорт выбранной версии: части, целостность, сборка, порядок классов, состав, примечание о
 * неизменяемости; действия «Проверить целостность», «Экспорт ZIP», «Изменить», «В архив» / «Восстановить», «Удалить датасет».
 * Архивную версию нельзя проверить, экспортировать и изменить; повреждённую — экспортировать. Ниже — примеры по частям.
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
    const archived = !!version.archivedAt;
    const corrupt = version.integrityState === "corrupt";
    const { composition } = version;
    return (
        <div className="space-y-4" data-testid="dataset-details" data-dataset-details-id={version.id} data-integrity={version.integrityState}>
            <div className="space-y-2 text-sm text-muted-foreground" data-testid="dataset-passport">
                {version.description && <p className="whitespace-pre-wrap break-words text-foreground">{version.description}</p>}
                <p data-testid="dataset-counts">
                    Готовые кадры · Train {count(version.counts.train)} · Validation {count(version.counts.val)} · Test {count(version.counts.test)}
                </p>
                <p data-testid="dataset-integrity">
                    Целостность: <span className={corrupt ? "text-destructive" : undefined}>{integrityLabels[version.integrityState]}</span>
                    {version.verifiedAt && ` (${formatDate(version.verifiedAt)})`} · {archived ? datasetTexts.archived : "Активен"}
                </p>
                <p data-testid="dataset-built">
                    Собран {formatDate(version.createdAt)} · Групп-источников: {version.specification.groupIds.length} · Seed {version.specification.seed}
                </p>
                <p data-testid="dataset-class-order">Сохранённый порядок классов: {classOrderText(version)}</p>
                <p>
                    Размер кадра: {version.width} × {version.height}
                    {!version.inputSizeMatches && <span className="text-amber-700 dark:text-amber-400" data-testid="dataset-details-size-mismatch"> · {datasetTexts.sizeMismatch}</span>}
                </p>
                <details className="rounded-md border px-3 py-2" data-testid="dataset-composition">
                    <summary className="cursor-pointer text-foreground">Состав</summary>
                    <div className="mt-2 space-y-1 text-xs">
                        <p>
                            Кадров без рамок: {count(composition.withoutBoxes)} · из исходных изображений: {count(composition.fromManual)} · сгенерированных: {count(composition.fromGenerated)}
                        </p>
                        <p>Рамок: {count(composition.boxes)} · негативов: {count(composition.negatives)}</p>
                        <p>Рамок по классам: {composition.boxesPerClass.map(item => `${item.index}: ${item.name} — ${count(item.boxes)}`).join(", ") || "—"}</p>
                        <p className="break-words">Группы-источники: {composition.groups.map(item => `${item.name} — ${count(item.images)}`).join(", ") || "—"}</p>
                    </div>
                </details>
                <p>Датасет содержит неизменяемые копии кадров и разметки. Изменения исходных классов и изображений не меняют эту версию.</p>
                {archived && <p data-testid="dataset-archived-note">Датасет в архиве: восстановите его, чтобы проверить целостность, экспортировать или изменить.</p>}
                {corrupt && <p className="text-destructive" data-testid="dataset-corrupt-note">{datasetTexts.integrityHelp}</p>}
            </div>
            <div className="flex flex-wrap gap-2">
                <Button variant="outline" disabled={busy || archived} data-testid="dataset-verify" onClick={verify}><ShieldCheck />Проверить целостность</Button>
                <Button disabled={busy || archived || corrupt} data-testid="dataset-export" onClick={exportZip}><Download />Экспорт ZIP</Button>
                <Button variant="outline" disabled={busy || archived} data-testid="dataset-edit" onClick={edit}><Pencil />Изменить</Button>
                <Button variant="ghost" disabled={busy} data-testid="dataset-archive" onClick={toggleArchive}>
                    {archived ? <><ArchiveRestore />Восстановить</> : <><Archive />В архив</>}
                </Button>
                <Button variant="destructive" size="sm" className="self-center" disabled={busy} data-testid="dataset-delete" onClick={remove}><Trash2 />Удалить датасет</Button>
            </div>
            <DatasetSamples key={version.id} projectId={projectId} version={version} />
        </div>
    );
}
