import { Search } from "lucide-react";
import { datasetTexts } from "@/api/projects/datasets/dataset-rules";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { count, matchesSearch } from "@/lib/datasets/dataset-view";
import { formatDate } from "@/lib/format";

export const versionButtonId = (versionId: string) => `dataset-version-${versionId}`;

/**
 * «Готовые датасеты»: поиск по названию и описанию, «Показать архив датасетов» и строки «name · В архиве», «N изображений ·
 * дата»; версия с размером не как вход проекта помечена «Размер отличается от входа проекта». Выбор — aria-pressed.
 */
export function DatasetList({ versions, loading, selectedId, select, search, setSearch, showArchived, setShowArchived }: {
    versions: readonly DatasetVersionResponseDto[];
    loading: boolean;
    selectedId: string | undefined;
    select: (versionId: string) => void;
    search: string;
    setSearch: (value: string) => void;
    showArchived: boolean;
    setShowArchived: (value: boolean) => void;
}) {
    const archived = versions.filter(version => version.archivedAt).length;
    const visible = versions.filter(version => (showArchived || !version.archivedAt) && matchesSearch(version, search));
    return (
        <section className="flex min-h-0 flex-col rounded-md border bg-card" aria-label="Готовые датасеты" data-testid="dataset-list">
            <div className="space-y-2 border-b p-3">
                <h2 className="font-medium">Готовые датасеты</h2>
                <div className="relative">
                    <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
                    <Input type="search" className="pl-8" placeholder="Поиск по названию" aria-label="Поиск датасетов" data-testid="dataset-search"
                        value={search} onChange={event => setSearch(event.target.value)} />
                </div>
                <label className="flex items-center gap-2 text-xs text-muted-foreground">
                    <Checkbox checked={showArchived} data-testid="dataset-show-archived" onCheckedChange={value => setShowArchived(value === true)} />
                    Показать архив датасетов{archived > 0 && ` (${count(archived)})`}
                </label>
            </div>
            <div className="min-h-0 flex-1 space-y-1 overflow-y-auto overscroll-contain p-2">
                {visible.map(version => (
                    <button
                        key={version.id}
                        id={versionButtonId(version.id)}
                        type="button"
                        data-dataset-version-id={version.id}
                        data-archived={version.archivedAt ? "true" : undefined}
                        aria-pressed={selectedId === version.id}
                        className={`w-full rounded-md border p-3 text-left outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring ${selectedId === version.id ? "border-primary bg-accent" : "border-transparent"}`}
                        onClick={() => select(version.id)}
                    >
                        <p className="text-sm font-medium break-words">{version.name}{version.archivedAt && ` · ${datasetTexts.archived}`}</p>
                        <p className="mt-1 text-xs text-muted-foreground">{count(version.counts.total)} изображений · {formatDate(version.createdAt)}</p>
                        {!version.inputSizeMatches && (
                            <p className="mt-1 text-xs text-amber-700 dark:text-amber-400" data-testid="dataset-size-mismatch">{datasetTexts.sizeMismatch}</p>
                        )}
                        {version.integrityState === "corrupt" && <p className="mt-1 text-xs text-destructive">Целостность нарушена</p>}
                    </button>
                ))}
                {loading && !versions.length && <p role="status" className="p-3 text-sm text-muted-foreground">{datasetTexts.loading}</p>}
                {!loading && !versions.length && (
                    <div className="flex min-h-32 items-center justify-center rounded-md border border-dashed p-4 text-center text-sm text-muted-foreground" data-testid="datasets-empty">
                        {datasetTexts.emptyList}
                    </div>
                )}
                {!loading && versions.length > 0 && !visible.length && (
                    <p className="p-3 text-sm text-muted-foreground" data-testid="datasets-nothing-found">
                        {search.trim() ? "Ничего не найдено. Измените запрос." : "Все датасеты в архиве. Включите «Показать архив датасетов»."}
                    </p>
                )}
            </div>
        </section>
    );
}
