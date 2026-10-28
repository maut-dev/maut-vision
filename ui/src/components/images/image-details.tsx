import type { ImageResponseDto } from "@/api/projects/images/response-dto/image-response-dto";
import { boxInPixels, type Box } from "@/lib/annotation/geometry";
import type { AnnotationDraft } from "@/lib/annotation/draft";
import { roiColor } from "@/lib/variant-color";
import type { AnnotationPalette } from "./annotation-palette";
import type { ViewerOverlay } from "@/components/viewer/image-viewer";
import { locale } from "@/lib/format";

export function sourceLabel(image: ImageResponseDto): string {
    const lineage = image.lineage;
    if (lineage.kind === "generated") return `Генерация · запуск ${lineage.generationRunId ?? "—"} · кадр ${lineage.sampleIndex ?? "—"}`;
    if (lineage.kind === "video") return `Видео ${lineage.videoName ?? "—"} · ${((lineage.timestampMs ?? 0) / 1000).toFixed(2)} с`;
    return "Импорт файла";
}

/** Рамки разметки для просмотрщика: объекты в цветах варианта или класса, затем ROI. */
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
    if (roi && annotation.roi) overlays.push({ id: "roi", box: annotation.roi, color: roiColor, label: "Область ROI", kind: "roi" });
    return overlays;
}

/** «{name} · W × H px · N КБ», источник, разметка и рамки в пикселях. */
export function ImageDetails({ image, annotation, revision, palette }: {
    image: ImageResponseDto;
    annotation?: AnnotationDraft;
    /** Ревизия разметки, к которой относится annotation. */
    revision?: number;
    palette: AnnotationPalette;
}) {
    const coordinates = (box: Box) => {
        const pixels = boxInPixels(box, image.width, image.height);
        return `x ${pixels.x}, y ${pixels.y}, ширина ${pixels.width}, высота ${pixels.height} px`;
    };
    return (
        <div className="space-y-1 text-xs text-muted-foreground" data-testid="image-details">
            <p className="break-words">{image.displayName} · {image.width} × {image.height} px · {Math.ceil(image.byteSize / 1024).toLocaleString(locale())} КБ</p>
            <p className="break-words">Источник: {sourceLabel(image)}</p>
            {annotation ? (
                <>
                    <p>
                        {annotation.objects.length} объектов · {annotation.reviewState === "reviewed" ? "Проверено" : "Не проверено"}
                        {revision !== undefined && ` · Ревизия разметки ${revision}`}
                    </p>
                    <ul className="max-h-32 space-y-1 overflow-auto" aria-label="Подробности разметки">
                        {annotation.objects.map((item, index) => (
                            <li key={item.id} className="break-words">{index + 1}. {palette.labelOf(item.classId, item.classVariantId)} · {coordinates(item.box)}</li>
                        ))}
                        {annotation.roi && <li>Область ROI · {coordinates(annotation.roi)}</li>}
                    </ul>
                </>
            ) : <p>Разметка загружается…</p>}
        </div>
    );
}
