import type { BackgroundImportPreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-import-preview-response-dto";
import { blurSummary } from "@/lib/backgrounds/background-text";
import { formatRadius } from "@/lib/backgrounds/blur-form";
import { backgroundReasonText } from "@/lib/errors";
import { locale } from "@/lib/format";

/**
 * «Примеры будущих фонов»: до 6 PNG ровно размера набора с теми же рамками, радиусами и пикселями, что даст запуск
 * той же операции; пропущенный источник показывает причину.
 */
export function BackgroundImportSamples({ preview }: { preview?: BackgroundImportPreviewResponseDto }) {
    if (!preview) return null;
    return (
        <div className="space-y-2" data-testid="import-samples">
            <p className="text-xs text-muted-foreground">
                Примеры будущих фонов {preview.targetSize.width} × {preview.targetSize.height}
                {preview.blur ? ` · ${blurSummary(preview.blur)}` : " · без размытия"}
            </p>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
                {preview.samples.map((sample, index) => (
                    <figure key={`${sample.sourceIndex}-${sample.timestampMs ?? ""}-${index}`} className="space-y-1 rounded-md border bg-card p-2" data-testid="import-sample">
                        {sample.imageUrl ? (
                            <img src={sample.imageUrl} alt={`Пример фона из ${sample.sourceName}`} draggable={false}
                                className="block aspect-square w-full rounded-sm bg-muted object-contain" />
                        ) : (
                            <div className="flex aspect-square w-full items-center justify-center rounded-sm bg-muted p-2 text-center text-xs text-destructive">
                                {sample.reason ? backgroundReasonText(sample.reason, sample.actualWidth, sample.actualHeight, preview.targetSize) : "Не удалось прочитать изображение"}
                            </div>
                        )}
                        <figcaption className="truncate text-xs text-muted-foreground" title={sample.sourceName}>
                            {sample.sourceName}
                            {sample.timestampMs !== null && ` · ${(sample.timestampMs / 1000).toLocaleString(locale(), { maximumFractionDigits: 3 })} с`}
                            {sample.blurRadius !== null && ` · ${formatRadius(sample.blurRadius)} px`}
                        </figcaption>
                    </figure>
                ))}
            </div>
        </div>
    );
}
