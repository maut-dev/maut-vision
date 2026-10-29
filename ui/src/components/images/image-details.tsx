import { useTranslation } from "react-i18next";
import type { ImageResponseDto } from "@/api/projects/images/response-dto/image-response-dto";
import { i18n } from "@/i18n";
import { boxInPixels, type Box } from "@/lib/annotation/geometry";
import type { AnnotationDraft } from "@/lib/annotation/draft";
import { roiColor } from "@/lib/variant-color";
import type { AnnotationPalette } from "./annotation-palette";
import type { ViewerOverlay } from "@/components/viewer/image-viewer";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/images";

export function sourceLabel(image: ImageResponseDto): string {
    const lineage = image.lineage;
    if (lineage.kind === "generated") return i18n.t("images:source.generated", { run: lineage.generationRunId ?? "—", sample: lineage.sampleIndex ?? "—" });
    if (lineage.kind === "video") return i18n.t("images:source.video", { name: lineage.videoName ?? "—", seconds: ((lineage.timestampMs ?? 0) / 1000).toFixed(2) });
    return i18n.t("images:source.file");
}

/** Annotation boxes for the viewer: objects in the colors of their variant or class, then the ROI. */
export function annotationOverlays(annotation: AnnotationDraft | undefined, palette: AnnotationPalette, objects: boolean, roi: boolean): ViewerOverlay[] {
    if (!annotation) return [];
    const overlays: ViewerOverlay[] = objects
        ? annotation.objects.map(item => ({
            id: item.id,
            box: item.box,
            color: palette.colorOf(item.classId, item.classVariantId),
            label: palette.labelOf(item.classId, item.classVariantId),
            kind: "object",
        }))
        : [];
    if (roi && annotation.roi) overlays.push({ id: "roi", box: annotation.roi, color: roiColor, label: i18n.t("images:roi"), kind: "roi" });
    return overlays;
}

/** "{name} · W × H px · N KB", the source, the annotation and the boxes in pixels. */
export function ImageDetails({ image, annotation, revision, palette }: {
    image: ImageResponseDto;
    annotation?: AnnotationDraft;
    /** The annotation revision that annotation belongs to. */
    revision?: number;
    palette: AnnotationPalette;
}) {
    const { t } = useTranslation("images");
    const coordinates = (box: Box) => t("details.box", boxInPixels(box, image.width, image.height));
    return (
        <div className="space-y-1 text-xs text-muted-foreground" data-testid="image-details">
            <p className="break-words">{t("details.file", { name: image.displayName, width: image.width, height: image.height, kilobytes: formatNumber(Math.ceil(image.byteSize / 1024)) })}</p>
            <p className="break-words">{t("details.source", { source: sourceLabel(image) })}</p>
            {annotation ? (
                <>
                    <p>
                        {t("details.objects", { count: annotation.objects.length })} · {annotation.reviewState === "reviewed" ? t("review.reviewed") : t("review.unreviewed")}
                        {revision !== undefined && ` · ${t("details.revision", { revision })}`}
                    </p>
                    <ul className="max-h-32 space-y-1 overflow-auto" aria-label={t("details.label")}>
                        {annotation.objects.map((item, index) => (
                            <li key={item.id} className="break-words">{index + 1}. {palette.labelOf(item.classId, item.classVariantId)} · {coordinates(item.box)}</li>
                        ))}
                        {annotation.roi && <li>{t("roi")} · {coordinates(annotation.roi)}</li>}
                    </ul>
                </>
            ) : <p>{t("details.loading")}</p>}
        </div>
    );
}
