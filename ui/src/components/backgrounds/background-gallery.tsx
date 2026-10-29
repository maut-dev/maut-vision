import type { CSSProperties, MouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { ArchiveRestore, EyeOff, Trash2 } from "lucide-react";
import type { BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import { ResourceImage } from "@/components/resource-image";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { VirtualGrid } from "@/components/virtual-grid";
import { backgroundMeta } from "@/lib/backgrounds/background-text";
import "@/i18n/sections/backgrounds";

export interface BackgroundGalleryActions {
    toggle: (item: BackgroundResponseDto, index: number, range: boolean) => void;
    view: (item: BackgroundResponseDto) => void;
    setRemoved: (item: BackgroundResponseDto, removed: boolean) => void;
    remove: (item: BackgroundResponseDto) => void;
    renew: (itemId: string) => void;
    loadMore: () => void;
}

/** The gallery of a set: a virtual grid of cards with thumbnails from the resource channel. */
export function BackgroundGallery({ items, selected, hasMore, loading, busy, readOnly, actions }: {
    items: readonly BackgroundResponseDto[];
    selected: ReadonlySet<string>;
    hasMore: boolean;
    loading: boolean;
    busy: boolean;
    /** An archived set: "Remove"/"Restore" are unavailable, deletion is available. */
    readOnly: boolean;
    actions: BackgroundGalleryActions;
}) {
    const { t } = useTranslation("backgrounds");
    return (
        <VirtualGrid
            items={items}
            keyOf={item => item.id}
            minTileWidth={210}
            tileHeight={244}
            hasMore={hasMore}
            loading={loading}
            loadMore={actions.loadMore}
            label={t("gallery.label")}
            testId="background-gallery"
            loadingText={t("section.loadingBackgrounds")}
            className="min-h-60"
            renderTile={(item, index, style) => (
                <BackgroundTile item={item} index={index} checked={selected.has(item.id)} busy={busy} readOnly={readOnly} actions={actions} style={style} />
            )}
        />
    );
}

function BackgroundTile({ item, index, checked, busy, readOnly, actions, style }: {
    item: BackgroundResponseDto;
    index: number;
    checked: boolean;
    busy: boolean;
    readOnly: boolean;
    actions: BackgroundGalleryActions;
    style: CSSProperties;
}) {
    const { t } = useTranslation("backgrounds");
    const name = item.origin.sourceName;
    const removed = !!item.removedAt;
    return (
        <div data-background-id={item.id} data-testid="background-tile" data-selected={checked || undefined} data-removed={removed || undefined} style={style}
            className={`flex min-w-0 flex-col rounded-md border bg-card p-2 ${checked ? "border-primary/60 bg-accent/40" : ""}`}>
            <label className="mb-2 flex min-w-0 items-center gap-2 text-xs">
                <Checkbox
                    aria-label={t("gallery.selectLabel", { name })}
                    checked={checked}
                    onClick={(event: MouseEvent) => {
                        event.preventDefault();
                        actions.toggle(item, index, event.shiftKey);
                    }}
                />
                <span className="min-w-0 truncate" title={name}>{removed ? t("gallery.removedName", { name }) : name}</span>
            </label>
            {removed ? (
                <div className="flex h-32 items-center justify-center rounded-md bg-muted p-2 text-center text-xs text-muted-foreground">{t("gallery.restoreToView")}</div>
            ) : (
                <button
                    type="button"
                    className="block w-full rounded-md outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    aria-label={t("gallery.openLabel", { name })}
                    data-testid="background-open"
                    onClick={() => actions.view(item)}
                >
                    <ResourceImage src={item.thumbnailUrl} alt={name} fit="contain" className="h-32 w-full" onExpired={() => actions.renew(item.id)} />
                </button>
            )}
            <span className="mt-2 line-clamp-2 text-xs text-muted-foreground" data-testid="background-meta" title={backgroundMeta(item)}>{backgroundMeta(item)}</span>
            <div className="mt-auto flex gap-1 pt-2">
                <Button size="xs" variant="ghost" className="min-w-0 flex-1" disabled={busy || readOnly}
                    aria-label={t(removed ? "gallery.restoreLabel" : "gallery.removeLabel", { name })} data-testid="background-toggle-removed"
                    onClick={() => actions.setRemoved(item, !removed)}>
                    {removed ? <><ArchiveRestore />{t("gallery.restore")}</> : <><EyeOff />{t("gallery.remove")}</>}
                </Button>
                <Button size="xs" variant="ghost" className="text-destructive hover:text-destructive" disabled={busy} aria-label={t("gallery.deleteLabel", { name })}
                    title={t("gallery.deleteHint")} onClick={() => actions.remove(item)}>
                    <Trash2 />{t("gallery.delete")}
                </Button>
            </div>
        </div>
    );
}
