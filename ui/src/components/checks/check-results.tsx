import { useState } from "react";
import { useTranslation } from "react-i18next";
import type { LiveCheckResponseDto, LiveDetectionDto } from "@/api/projects/checks/response-dto/live-check-response-dto";
import type { ResourceUrl } from "@/api/resources/resource-url";
import { Badge } from "@/components/ui/badge";
import { classColor, formatConfidence } from "@/lib/checks/check-view";
import { codedText } from "@/lib/coded-text";
import { workerErrorText } from "@/lib/errors";
import { formatNumber } from "@/lib/format";

/** State of a live check: the label, the version and the file, the error, warnings, the stop reason. */
export function CheckStatus({ check, testPrefix }: { check: LiveCheckResponseDto; testPrefix: string }) {
    const { t } = useTranslation("checks");
    const parts = [t("status.version", { number: check.versionNumber })];
    if (check.source.name) parts.push(check.source.name);
    if (check.stopReason && check.state !== "failed") parts.push(t(`status.stopReason.${check.stopReason}`));
    return (
        <div className="space-y-1 text-sm" aria-live="polite">
            <p className="flex flex-wrap items-center gap-2">
                <Badge variant={check.state === "failed" ? "destructive" : "secondary"} data-testid={`${testPrefix}-state`} data-state={check.state}>
                    {t(`liveState.${check.state}`)}
                </Badge>
                <span className="text-muted-foreground">{parts.join(" · ")}</span>
            </p>
            {check.error && <p role="alert" className="text-destructive" data-testid={`${testPrefix}-error`}>{workerErrorText(check.error)}</p>}
            {check.warnings.map(warning => (
                <p key={warning.code} className="text-xs text-muted-foreground" data-testid={`${testPrefix}-warning`}>⚠ {codedText(warning.message)}</p>
            ))}
        </div>
    );
}

/** The frame preview with detection boxes: coordinates in fractions of the frame, the caption "name NN%". */
export function DetectionOverlay({ src, width, height, detections, alt, testId }: {
    src: ResourceUrl;
    width: number;
    height: number;
    detections: readonly LiveDetectionDto[];
    alt: string;
    testId: string;
}) {
    const { t } = useTranslation("checks");
    const [broken, setBroken] = useState<string>();
    const percent = (value: number) => `${value * 100}%`;
    return (
        <div className="relative mx-auto overflow-hidden rounded-md bg-muted" data-testid={testId} data-boxes={detections.length}
            style={{ aspectRatio: `${width} / ${height}`, width: `min(100%, calc(60vh * ${width / height}))` }}>
            {broken === src
                ? <div className="absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">{t("results.frameUnavailable")}</div>
                : <img src={src} alt={alt} draggable={false} className="absolute inset-0 block size-full" data-testid={`${testId}-image`} onError={() => setBroken(src)} />}
            {detections.map((item, index) => (
                <div key={index} className="pointer-events-none absolute border-2" data-detection={item.name}
                    style={{ left: percent(item.x), top: percent(item.y), width: percent(item.width), height: percent(item.height), borderColor: classColor(item.classIndex) }}>
                    <span className="absolute left-[-2px] top-0 -translate-y-full whitespace-nowrap rounded-sm px-1 text-[11px] font-medium leading-4 text-white"
                        style={{ background: classColor(item.classIndex) }}>
                        {item.name} {formatConfidence(item.confidence)}
                    </span>
                </div>
            ))}
        </div>
    );
}

export function DetectionChips({ detections }: { detections: readonly LiveDetectionDto[] }) {
    const { t } = useTranslation("checks");
    if (!detections.length) return <span className="text-xs text-muted-foreground">{t("results.nothing")}</span>;
    return (
        <span className="flex flex-wrap gap-1">
            {detections.map((item, index) => (
                <span key={index} className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs" data-chip>
                    <span className="inline-block size-2 rounded-full" style={{ background: classColor(item.classIndex) }} />
                    {item.name}
                    <span className="tabular-nums text-muted-foreground">{formatConfidence(item.confidence)}</span>
                </span>
            ))}
        </span>
    );
}

/** The per-class summary: class, frames, boxes, max confidence. */
export function ClassSummary({ check, testId }: { check: LiveCheckResponseDto; testId: string }) {
    const { t } = useTranslation("checks");
    const classes = check.summary.classes;
    if (!classes.length) return <p className="text-sm text-muted-foreground" data-testid={testId}>{t("results.empty")}</p>;
    return (
        <div className="overflow-x-auto rounded-md border" data-testid={testId}>
            <table className="w-full text-sm">
                <thead>
                    <tr className="border-b text-left text-xs text-muted-foreground">
                        <th className="px-3 py-2 font-normal">{t("results.class")}</th>
                        <th className="px-3 py-2 text-right font-normal">{t("results.frames")}</th>
                        <th className="px-3 py-2 text-right font-normal">{t("results.boxes")}</th>
                        <th className="px-3 py-2 text-right font-normal">{t("results.maxConfidence")}</th>
                    </tr>
                </thead>
                <tbody>
                    {classes.map(item => (
                        <tr key={item.classIndex} className="border-b last:border-b-0" data-class-index={item.classIndex}>
                            <td className="px-3 py-2">
                                <span className="inline-flex items-center gap-1.5">
                                    <span className="inline-block size-2 rounded-full" style={{ background: classColor(item.classIndex) }} />
                                    {item.name}
                                </span>
                            </td>
                            <td className="px-3 py-2 text-right tabular-nums">{formatNumber(item.frames)}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{formatNumber(item.detections)}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{formatConfidence(item.maxConfidence)}</td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    );
}

/** Detections of the preview frame: from the last frame or the journal; a frame without detections gives none. */
export function previewDetections(check: LiveCheckResponseDto, journal: readonly { index: number; detections: LiveDetectionDto[] }[]): LiveDetectionDto[] {
    const index = check.preview?.frameIndex;
    if (index === undefined) return [];
    if (check.lastFrame?.index === index) return check.lastFrame.detections;
    return journal.find(row => row.index === index)?.detections ?? [];
}
