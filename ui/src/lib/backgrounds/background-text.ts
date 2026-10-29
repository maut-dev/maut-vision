import type { BackgroundOriginKind, BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import "@/components/backgrounds/locale";
import { i18n } from "@/i18n";
import { formatNumber } from "@/lib/format";
import { formatRadius } from "./blur-form";

// Lines of the backgrounds section built from values. They are called during render, so a language change renders them again.

/** Labels of the origins in the current language. */
export const originLabels: Readonly<Record<BackgroundOriginKind, string>> = {
    get "uploaded"() { return i18n.t("backgrounds:origin.uploaded"); },
    get "image-crop"() { return i18n.t("backgrounds:origin.image-crop"); },
    get "video-frame"() { return i18n.t("backgrounds:origin.video-frame"); },
};

/** "N backgrounds · W × H[ · Archived]". */
export function setSummary(set: BackgroundSetResponseDto): string {
    const summary = i18n.t("backgrounds:summary.set", { count: set.itemCount, width: set.targetSize.width, height: set.targetSize.height });
    return set.archivedAt ? `${summary} · ${i18n.t("backgrounds:summary.archived")}` : summary;
}

/** "The set size W × H differs from the project input". */
export function sizeMismatchText(set: BackgroundSetResponseDto): string {
    return i18n.t("backgrounds:summary.sizeMismatch", { width: set.targetSize.width, height: set.targetSize.height });
}

/** The card caption: "W × H · cropped at x L, y T · blur r px". */
export function backgroundMeta(item: BackgroundResponseDto): string {
    const parts = [`${item.width} × ${item.height}`];
    if (item.origin.crop) parts.push(i18n.t("backgrounds:meta.cropped", { left: item.origin.crop.left, top: item.origin.crop.top }));
    if (item.origin.blurRadius !== null) parts.push(i18n.t("backgrounds:meta.blur", { radius: formatRadius(item.origin.blurRadius) }));
    return parts.join(" · ");
}

/** Detail lines of the viewer. */
export function backgroundDetails(item: BackgroundResponseDto): string[] {
    const { origin } = item;
    const kilobytes = formatNumber(item.byteSize / 1024, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
    const lines = [i18n.t("backgrounds:details.source", { origin: originLabels[origin.kind], kilobytes })];
    if (origin.kind === "video-frame" && origin.timestampMs !== null) {
        const values = { name: origin.videoName ?? origin.sourceName, seconds: (origin.timestampMs / 1000).toFixed(3) };
        lines.push(origin.frameIndex !== null
            ? i18n.t("backgrounds:details.videoFrame", { ...values, frame: origin.frameIndex })
            : i18n.t("backgrounds:details.video", values));
    }
    if (origin.crop) lines.push(i18n.t("backgrounds:details.region", { ...origin.crop }));
    if (origin.blurRadius !== null) lines.push(i18n.t("backgrounds:details.blur", { radius: formatRadius(origin.blurRadius) }));
    return lines;
}

/** "blur a–b px, seed S". */
export function blurSummary(blur: { minRadius: number; maxRadius: number; seed: number }): string {
    return i18n.t("backgrounds:blur.summary", { min: formatRadius(blur.minRadius), max: formatRadius(blur.maxRadius), seed: blur.seed });
}
