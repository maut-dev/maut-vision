import { useState } from "react";
import type { LiveCheckResponseDto, LiveDetectionDto } from "@/api/projects/checks/response-dto/live-check-response-dto";
import { liveCheckStateLabels } from "@/api/projects/checks/response-dto/live-check-response-dto";
import type { ResourceUrl } from "@/api/resources/resource-url";
import { Badge } from "@/components/ui/badge";
import { classColor, formatConfidence } from "@/lib/checks/check-view";

const stopReasons: Record<NonNullable<LiveCheckResponseDto["stopReason"]>, string> = {
    user: "остановлена кнопкой",
    project_switched: "остановлена при переходе в другой проект",
    session_ended: "остановлена перезагрузкой страницы",
    time_limit: "остановлена по пределу времени",
};

/** Состояние живой проверки: подпись, версия и файл, ошибка, предупреждения, причина остановки. */
export function CheckStatus({ check, testPrefix }: { check: LiveCheckResponseDto; testPrefix: string }) {
    return (
        <div className="space-y-1 text-sm" aria-live="polite">
            <p className="flex flex-wrap items-center gap-2">
                <Badge variant={check.state === "failed" ? "destructive" : "secondary"} data-testid={`${testPrefix}-state`} data-state={check.state}>
                    {liveCheckStateLabels[check.state]}
                </Badge>
                <span className="text-muted-foreground">
                    версия {check.versionNumber}{check.source.name ? ` · ${check.source.name}` : ""}
                    {check.stopReason && check.state !== "failed" ? ` · ${stopReasons[check.stopReason]}` : ""}
                </span>
            </p>
            {check.error && <p role="alert" className="text-destructive" data-testid={`${testPrefix}-error`}>{check.error.message}</p>}
            {check.warnings.map(warning => (
                <p key={warning.code} className="text-xs text-muted-foreground" data-testid={`${testPrefix}-warning`}>⚠ {warning.message}</p>
            ))}
        </div>
    );
}

/** Превью кадра с рамками обнаружений: координаты в долях кадра, подпись «имя NN%». */
export function DetectionOverlay({ src, width, height, detections, alt, testId }: {
    src: ResourceUrl;
    width: number;
    height: number;
    detections: readonly LiveDetectionDto[];
    alt: string;
    testId: string;
}) {
    const [broken, setBroken] = useState<string>();
    const percent = (value: number) => `${value * 100}%`;
    return (
        <div className="relative mx-auto overflow-hidden rounded-md bg-muted" data-testid={testId} data-boxes={detections.length}
            style={{ aspectRatio: `${width} / ${height}`, width: `min(100%, calc(60vh * ${width / height}))` }}>
            {broken === src
                ? <div className="absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">Не удалось показать кадр.</div>
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
    if (!detections.length) return <span className="text-xs text-muted-foreground">ничего</span>;
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

/** «Итог по классам»: Класс / Кадров / Рамок / Макс. уверенность. */
export function ClassSummary({ check, testId }: { check: LiveCheckResponseDto; testId: string }) {
    const classes = check.summary.classes;
    if (!classes.length) return <p className="text-sm text-muted-foreground" data-testid={testId}>Обнаружений пока нет.</p>;
    return (
        <div className="overflow-x-auto rounded-md border" data-testid={testId}>
            <table className="w-full text-sm">
                <thead>
                    <tr className="border-b text-left text-xs text-muted-foreground">
                        <th className="px-3 py-2 font-normal">Класс</th>
                        <th className="px-3 py-2 text-right font-normal">Кадров</th>
                        <th className="px-3 py-2 text-right font-normal">Рамок</th>
                        <th className="px-3 py-2 text-right font-normal">Макс. уверенность</th>
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
                            <td className="px-3 py-2 text-right tabular-nums">{item.frames.toLocaleString("ru-RU")}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{item.detections.toLocaleString("ru-RU")}</td>
                            <td className="px-3 py-2 text-right tabular-nums">{formatConfidence(item.maxConfidence)}</td>
                        </tr>
                    ))}
                </tbody>
            </table>
        </div>
    );
}

/** Детектирования кадра превью: из последнего кадра или журнала; кадр без обнаружений — пусто. */
export function previewDetections(check: LiveCheckResponseDto, journal: readonly { index: number; detections: LiveDetectionDto[] }[]): LiveDetectionDto[] {
    const index = check.preview?.frameIndex;
    if (index === undefined) return [];
    if (check.lastFrame?.index === index) return check.lastFrame.detections;
    return journal.find(row => row.index === index)?.detections ?? [];
}
