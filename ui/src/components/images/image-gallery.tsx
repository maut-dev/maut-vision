import type { CSSProperties, MouseEvent } from "react";
import { useTranslation } from "react-i18next";
import { Trash2 } from "lucide-react";
import type { ImageAnnotationResponseDto } from "@/api/projects/images/response-dto/image-annotation-response-dto";
import type { ImageResponseDto } from "@/api/projects/images/response-dto/image-response-dto";
import { ResourceImage } from "@/components/resource-image";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { VirtualGrid } from "@/components/virtual-grid";
import { roiColor } from "@/lib/variant-color";
import type { AnnotationPalette } from "./annotation-palette";
import "./locale";

export interface GalleryActions {
    toggle: (image: ImageResponseDto, index: number, range: boolean) => void;
    edit: (image: ImageResponseDto) => void;
    view: (image: ImageResponseDto) => void;
    remove: (image: ImageResponseDto) => void;
    renew: (imageId: string) => void;
    loadMore: () => void;
}

/** The gallery of a group: a virtual grid (VirtualGrid) of tiles with a thumbnail and annotation boxes. */
export function ImageGallery({ items, annotations, selected, hasMore, loading, locked, busy, palette, actions }: {
    items: readonly ImageResponseDto[];
    annotations: Readonly<Record<string, ImageAnnotationResponseDto>>;
    selected: ReadonlySet<string>;
    hasMore: boolean;
    loading: boolean;
    locked: boolean;
    busy: boolean;
    palette: AnnotationPalette;
    actions: GalleryActions;
}) {
    const { t } = useTranslation("images");
    return (
        <VirtualGrid
            items={items}
            keyOf={image => image.id}
            minTileWidth={200}
            tileHeight={262}
            hasMore={hasMore}
            loading={loading}
            loadMore={actions.loadMore}
            label={t("gallery.label")}
            testId="image-gallery"
            loadingText={t("section.loadingImages")}
            renderTile={(image, index, style) => (
                <ImageTile
                    image={image}
                    index={index}
                    annotation={annotations[image.id]}
                    checked={selected.has(image.id)}
                    locked={locked}
                    busy={busy}
                    palette={palette}
                    actions={actions}
                    style={style}
                />
            )}
        />
    );
}

function ImageTile({ image, index, annotation, checked, locked, busy, palette, actions, style }: {
    image: ImageResponseDto;
    index: number;
    annotation?: ImageAnnotationResponseDto;
    checked: boolean;
    locked: boolean;
    busy: boolean;
    palette: AnnotationPalette;
    actions: GalleryActions;
    style: CSSProperties;
}) {
    const { t } = useTranslation("images");
    const meta = [
        `${image.width} × ${image.height}`,
        image.objectCount ? t("gallery.boxes", { number: image.objectCount }) : t("gallery.noBoxes"),
        image.reviewState === "reviewed" ? t("gallery.reviewed") : null,
        image.archivedAt ? t("gallery.archived") : null,
    ].filter(Boolean).join(" · ");
    return (
        <div data-image-id={image.id} data-testid="image-tile" data-selected={checked || undefined} style={style}
            className={`flex min-w-0 flex-col rounded-md border bg-card p-2 ${checked ? "border-primary/60 bg-accent/40" : ""}`}>
            <label className="mb-2 flex items-center gap-2 text-xs text-muted-foreground">
                <Checkbox
                    aria-label={t("gallery.selectLabel", { name: image.displayName })}
                    checked={checked}
                    disabled={locked}
                    onClick={(event: MouseEvent) => {
                        event.preventDefault();
                        actions.toggle(image, index, event.shiftKey);
                    }}
                />
                {t("gallery.select")}
            </label>
            <button
                type="button"
                className="min-w-0 rounded-md text-left outline-none transition-colors hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring"
                aria-label={t("gallery.annotateLabel", { name: image.displayName })}
                data-testid="image-open"
                onClick={() => actions.edit(image)}
            >
                <div className="relative">
                    <ResourceImage src={image.thumbnailUrl} alt={image.displayName} fit="contain" className="h-36 w-full" onExpired={() => actions.renew(image.id)} />
                    <AnnotationOverlay image={image} annotation={annotation} palette={palette} />
                </div>
                <span className="mt-2 block truncate text-xs" title={image.displayName}>{image.displayName}</span>
                <span className="block truncate text-xs text-muted-foreground" data-testid="image-meta">{meta}</span>
            </button>
            <div className="mt-auto flex gap-2 pt-2">
                <Button size="sm" variant="outline" className="min-w-0 flex-1" aria-label={t("gallery.viewLabel", { name: image.displayName })} onClick={() => actions.view(image)}>{t("gallery.view")}</Button>
                <Button size="sm" variant="ghost" disabled={busy || locked} aria-label={t("gallery.deleteLabel", { name: image.displayName })} title={t("gallery.deleteHint")} onClick={() => actions.remove(image)}>
                    <Trash2 />
                </Button>
            </div>
        </div>
    );
}

/** Boxes over the thumbnail: a viewBox in frame pixels with meet repeats object-contain. */
function AnnotationOverlay({ image, annotation, palette }: { image: ImageResponseDto; annotation?: ImageAnnotationResponseDto; palette: AnnotationPalette }) {
    if (!annotation || (!annotation.objects.length && !annotation.roi)) return null;
    const { width, height } = image;
    const dash = `${Math.max(width, height) / 60} ${Math.max(width, height) / 90}`;
    return (
        <svg className="pointer-events-none absolute inset-0 size-full" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="xMidYMid meet" aria-hidden data-testid="tile-overlay">
            {annotation.roi && (
                <rect x={annotation.roi.x * width} y={annotation.roi.y * height} width={annotation.roi.width * width} height={annotation.roi.height * height}
                    fill="none" stroke={roiColor} strokeWidth={1.5} strokeDasharray={dash} vectorEffect="non-scaling-stroke" />
            )}
            {annotation.objects.map(item => {
                const color = palette.colorOf(item.classId, item.classVariantId);
                return (
                    <rect key={item.id} x={item.box.x * width} y={item.box.y * height} width={item.box.width * width} height={item.box.height * height}
                        fill={color} fillOpacity={0.15} stroke={color} strokeWidth={2} vectorEffect="non-scaling-stroke" />
                );
            })}
        </svg>
    );
}
