import { Search } from "lucide-react";
import { useTranslation } from "react-i18next";
import { datasetTexts } from "@/api/projects/datasets/dataset-rules";
import type { DatasetVersionResponseDto } from "@/api/projects/datasets/response-dto/dataset-response-dto";
import { CollectionItem } from "@/components/collections/collection-list";
import { EmptyState } from "@/components/layout/empty-state";
import { Pane } from "@/components/layout/pane";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { count, matchesSearch } from "@/lib/datasets/dataset-view";
import { formatDate } from "@/lib/format";

export const versionButtonId = (versionId: string) => `dataset-version-${versionId}`;

/**
 * "Finished datasets": search by name and description and "Show dataset archive" above one scrolling list of rows "name · Archived",
 * "N images · date"; a version whose size differs from the project input is marked "The size differs from the project input". The
 * choice is aria-pressed.
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
    const { t } = useTranslation("datasets");
    const archived = versions.filter(version => version.archivedAt).length;
    const visible = versions.filter(version => (showArchived || !version.archivedAt) && matchesSearch(version, search));
    return (
        <section className="flex min-h-0 min-w-0 flex-col gap-2" aria-label={t("list.title")} data-testid="dataset-list">
            <div className="shrink-0 space-y-2">
                <div className="relative">
                    <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-text-3" aria-hidden />
                    <Input type="search" className="pl-8" placeholder={t("list.searchPlaceholder")} aria-label={t("list.search")} data-testid="dataset-search"
                        value={search} onChange={event => setSearch(event.target.value)} />
                </div>
                <label className="flex items-center gap-2 text-xs text-text-2">
                    <Checkbox checked={showArchived} data-testid="dataset-show-archived" onCheckedChange={value => setShowArchived(value === true)} />
                    {archived > 0 ? t("list.showArchivedCount", { number: count(archived) }) : t("list.showArchived")}
                </label>
            </div>
            <Pane surface role="region" aria-label={t("list.title")} tabIndex={0} className="flex-1 p-2 outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30">
                <div className="space-y-1">
                    {visible.map(version => (
                        <CollectionItem
                            key={version.id}
                            id={versionButtonId(version.id)}
                            data-dataset-version-id={version.id}
                            data-archived={version.archivedAt ? "true" : undefined}
                            selected={selectedId === version.id}
                            onClick={() => select(version.id)}
                            title={`${version.name}${version.archivedAt ? ` · ${datasetTexts.archived}` : ""}`}
                            summary={(
                                <span className="mt-1 block text-xs text-text-2" data-testid="dataset-version-summary">
                                    {t("list.images", { count: version.counts.total, value: count(version.counts.total), date: formatDate(version.createdAt) })}
                                </span>
                            )}
                        >
                            {!version.inputSizeMatches && <span className="mt-1 block text-xs text-warn" data-testid="dataset-size-mismatch">{datasetTexts.sizeMismatch}</span>}
                            {version.integrityState === "corrupt" && <span className="mt-1 block text-xs text-destructive">{t("list.corrupt")}</span>}
                        </CollectionItem>
                    ))}
                    {loading && !versions.length && <p role="status" className="p-2 text-sm text-text-2">{datasetTexts.loading}</p>}
                    {!loading && !versions.length && (
                        <EmptyState title={datasetTexts.emptyList} className="rounded-md border border-dashed border-line px-3 py-6" data-testid="datasets-empty" />
                    )}
                    {!loading && versions.length > 0 && !visible.length && (
                        <p className="p-2 text-sm text-text-2" data-testid="datasets-nothing-found">
                            {search.trim() ? t("list.nothingFound") : t("list.allArchived")}
                        </p>
                    )}
                </div>
            </Pane>
        </section>
    );
}
