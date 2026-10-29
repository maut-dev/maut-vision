import { useTranslation } from "react-i18next";
import type { BackgroundImportPreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-import-preview-response-dto";
import { blurSummary } from "@/lib/backgrounds/background-text";
import { formatRadius } from "@/lib/backgrounds/blur-form";
import { backgroundReasonText } from "@/lib/errors";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/backgrounds";

/**
 * "Samples of future backgrounds": up to 6 PNGs of exactly the set size with the same frames, radii and pixels the run of the same
 * operation gives; a skipped source shows its reason.
 */
export function BackgroundImportSamples({ preview }: { preview?: BackgroundImportPreviewResponseDto }) {
    const { t } = useTranslation("backgrounds");
    if (!preview) return null;
    return (
        <div className="space-y-2" data-testid="import-samples">
            <p className="text-xs text-muted-foreground">
                {t("samples.title", { width: preview.targetSize.width, height: preview.targetSize.height })}
                {` · ${preview.blur ? blurSummary(preview.blur) : t("samples.noBlur")}`}
            </p>
            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
                {preview.samples.map((sample, index) => (
                    <figure key={`${sample.sourceIndex}-${sample.timestampMs ?? ""}-${index}`} className="space-y-1 rounded-md border bg-card p-2" data-testid="import-sample">
                        {sample.imageUrl ? (
                            <img src={sample.imageUrl} alt={t("samples.alt", { name: sample.sourceName })} draggable={false}
                                className="block aspect-square w-full rounded-sm bg-muted object-contain" />
                        ) : (
                            <div className="flex aspect-square w-full items-center justify-center rounded-sm bg-muted p-2 text-center text-xs text-destructive">
                                {sample.reason ? backgroundReasonText(sample.reason, sample.actualWidth, sample.actualHeight, preview.targetSize) : t("crop.unreadable")}
                            </div>
                        )}
                        <figcaption className="truncate text-xs text-muted-foreground" title={sample.sourceName}>
                            {sample.sourceName}
                            {sample.timestampMs !== null && ` · ${t("seconds", { seconds: formatNumber(sample.timestampMs / 1000, { maximumFractionDigits: 3 }) })}`}
                            {sample.blurRadius !== null && ` · ${formatRadius(sample.blurRadius)} px`}
                        </figcaption>
                    </figure>
                ))}
            </div>
        </div>
    );
}
