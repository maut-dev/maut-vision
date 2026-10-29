import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import type { GpuSampleDto, HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";
import { seriesColor } from "@/lib/checks/check-view";
import { appendLoad, loadSegments, loadWindowMs, type LoadPoint } from "@/lib/overview/load-history";
import { useHardwareSample } from "@/lib/use-hardware-sample";
import { i18n } from "@/i18n";
import "@/i18n/sections/overview";
import { formatClock, formatNumber } from "@/lib/format";
import { errorText } from "@/lib/errors";

/** Gigabytes with up to two decimals ("15.85 GB"); memory readings stay in GB even below 1 GB. */
const gigabytes = (bytes: number) => i18n.t("units.gigabytes", { value: formatNumber(bytes / 1024 ** 3, { maximumFractionDigits: 2 }) });

const percent = (value: number | null | undefined) =>
    typeof value === "number" ? `${formatNumber(value, { maximumFractionDigits: 1 })}%` : i18n.t("overview:hardware.unavailable");

/** The line color of the mini chart: CPU is the first series, GPU i the one after it. */
const gpuColor = (index: number) => seriesColor(index + 2);

/**
 * Your computer: CPU, RAM, the memory of the application with its parts, GPUs (load, memory, temperature, driver) and the mini chart of CPU
 * and GPU load over the last ~2 minutes. The sample comes from useHardwareSample (every ~2 s while the section is open and the window is
 * visible); the chart history lives in the page while the section is open.
 */
export function OverviewHardware() {
    const { t } = useTranslation("overview");
    const { sample, failed } = useHardwareSample();
    const [history, setHistory] = useState<readonly LoadPoint[]>([]);

    useEffect(() => {
        if (sample) setHistory(current => appendLoad(current, sample));
    }, [sample]);

    return (
        <section className="min-w-0 space-y-4 rounded-lg border bg-card p-4" aria-labelledby="overview-hardware-heading" data-testid="overview-hardware">
            <h2 id="overview-hardware-heading" className="font-medium">{t("hardware.title")}</h2>
            {failed && (
                <p role="alert" className="text-sm text-destructive" data-testid="overview-hardware-error">{t("hardware.failed")}</p>
            )}
            {!sample ? (
                <p role="status" className="text-sm text-muted-foreground">{t("hardware.collecting")}</p>
            ) : (
                <HardwareSample sample={sample} history={history} />
            )}
        </section>
    );
}

function HardwareSample({ sample, history }: { sample: HardwareSampleResponseDto; history: readonly LoadPoint[] }) {
    const { t } = useTranslation("overview");
    const { cpu, ram, application } = sample;
    const threads = t("hardware.threads", { count: cpu.logicalThreads, value: formatNumber(cpu.logicalThreads) });
    return (
        <>
            <div className="space-y-3" data-testid="overview-cpu" data-status={cpu.status}>
                <div>
                    <h3 className="break-words text-sm font-medium">{cpu.model ?? t("hardware.cpu")}</h3>
                    <p className="text-xs text-muted-foreground" data-testid="overview-cpu-threads">
                        {cpu.physicalCores !== null ? `${t("hardware.cores", { count: cpu.physicalCores, value: formatNumber(cpu.physicalCores) })} · ${threads}` : threads}
                    </p>
                </div>
                <Usage label={t("hardware.cpuLoad")} value={cpu.utilizationPercent} detail={cpu.status === "available" || !cpu.reason ? undefined : errorText(cpu.reason)}
                    testId="overview-cpu-load" />
            </div>
            <div className="space-y-2 border-t pt-4">
                {ram ? (
                    <Usage label={t("hardware.ramLoad")} value={ram.utilizationPercent} detail={t("hardware.used", { used: gigabytes(ram.usedBytes), total: gigabytes(ram.totalBytes) })}
                        testId="overview-ram" />
                ) : (
                    <Usage label={t("hardware.ramLoad")} value={null} testId="overview-ram" />
                )}
                <div className="text-xs text-muted-foreground" data-testid="overview-application">
                    <p className="text-sm text-foreground">{t("hardware.application", { size: gigabytes(application.totalBytes) })}</p>
                    <p data-testid="overview-application-parts">
                        {t("hardware.parts", {
                            host: gigabytes(application.hostBytes),
                            webViewCount: formatNumber(application.webViewProcesses),
                            webView: gigabytes(application.webViewBytes),
                            workerCount: formatNumber(application.workerProcesses),
                            worker: gigabytes(application.workerBytes),
                        })}
                    </p>
                </div>
            </div>
            <div className="space-y-4 border-t pt-4" data-testid="overview-gpus" data-status={sample.gpuStatus}>
                {sample.gpuStatus === "unavailable" || !sample.gpus.length ? (
                    <div>
                        <h3 className="text-sm font-medium">GPU</h3>
                        <p className="mt-2 text-xs text-muted-foreground" data-testid="overview-gpu-reason">
                            {sample.gpuReason ? errorText(sample.gpuReason) : t("hardware.unavailable")}
                        </p>
                    </div>
                ) : sample.gpus.map(gpu => <Gpu key={gpu.index} gpu={gpu} />)}
            </div>
            <LoadChart history={history} gpus={sample.gpus} />
            <p className="text-xs text-muted-foreground" data-testid="overview-hardware-time">{t("hardware.sampled", { time: formatClock(sample.timestamp) })}</p>
        </>
    );
}

function Gpu({ gpu }: { gpu: GpuSampleDto }) {
    const { t } = useTranslation("overview");
    const memory = gpu.memoryTotalBytes && gpu.memoryUsedBytes !== null ? (gpu.memoryUsedBytes / gpu.memoryTotalBytes) * 100 : null;
    const temperature = gpu.temperatureC === null ? t("hardware.unavailable") : `${formatNumber(gpu.temperatureC, { maximumFractionDigits: 0 })} °C`;
    return (
        <div className="space-y-3" data-testid="overview-gpu" data-gpu-index={gpu.index}>
            <h3 className="break-words text-sm font-medium">GPU {gpu.index} · {gpu.name}</h3>
            <Usage label={t("hardware.gpuLoad")} value={gpu.utilizationPercent} />
            <Usage
                label={t("hardware.gpuMemory")}
                value={memory}
                detail={gpu.memoryTotalBytes !== null && gpu.memoryUsedBytes !== null
                    ? t("hardware.used", { used: gigabytes(gpu.memoryUsedBytes), total: gigabytes(gpu.memoryTotalBytes) }) : undefined}
            />
            <p className="text-xs text-muted-foreground" data-testid="overview-gpu-details">
                {t("hardware.temperature", { value: temperature })}
                {" · "}{t("hardware.driver", { value: gpu.driverVersion ?? t("hardware.unavailable") })}
            </p>
        </div>
    );
}

function Usage({ label, value, detail, testId }: { label: string; value: number | null; detail?: string; testId?: string }) {
    return (
        <div className="min-w-0 space-y-1.5" data-testid={testId}>
            <div className="flex flex-wrap justify-between gap-2 text-sm">
                <span>{label}</span>
                <span className="tabular-nums" data-testid={testId && `${testId}-value`}>{percent(value)}</span>
            </div>
            {value !== null && (
                <div className="h-1.5 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(value)}>
                    <div className="h-full bg-brand transition-[width]" style={{ width: `${Math.min(100, value)}%` }} />
                </div>
            )}
            {detail && <p className="break-words text-xs text-muted-foreground" data-testid={testId && `${testId}-detail`}>{detail}</p>}
        </div>
    );
}

const chartWidth = 300;
const chartHeight = 64;

/** The mini chart of CPU and GPU load over the last ~2 minutes: the time axis is the window up to the last sample, the Y axis 0–100 %. */
function LoadChart({ history, gpus }: { history: readonly LoadPoint[]; gpus: readonly GpuSampleDto[] }) {
    const { t } = useTranslation("overview");
    const end = history.length ? history[history.length - 1].at : 0;
    const x = (at: number) => ((at - (end - loadWindowMs)) / loadWindowMs) * chartWidth;
    const y = (value: number) => chartHeight - (Math.min(100, Math.max(0, value)) / 100) * (chartHeight - 2) - 1;
    const lines = [
        { key: "cpu", label: "CPU", color: seriesColor(1), segments: loadSegments(history, point => point.cpu) },
        ...gpus.map(gpu => ({ key: `gpu-${gpu.index}`, label: `GPU ${gpu.index}`, color: gpuColor(gpu.index), segments: loadSegments(history, point => point.gpus[gpu.index]) })),
    ];
    return (
        <figure className="space-y-1.5 border-t pt-4" data-testid="overview-load-chart" data-points={history.length}>
            <figcaption className="flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                <span>{t("hardware.chartCaption")}</span>
                <span className="flex flex-wrap gap-3">
                    {lines.map(line => (
                        <span key={line.key} className="flex items-center gap-1.5">
                            <span className="inline-block h-0.5 w-3 rounded" style={{ background: line.color }} />
                            {line.label}
                        </span>
                    ))}
                </span>
            </figcaption>
            <svg viewBox={`0 0 ${chartWidth} ${chartHeight}`} preserveAspectRatio="none" className="h-16 w-full rounded border" role="img"
                aria-label={t("hardware.chartAria")}>
                {[25, 50, 75].map(tick => <line key={tick} x1={0} x2={chartWidth} y1={y(tick)} y2={y(tick)} stroke="var(--border)" strokeDasharray="3 3" />)}
                {lines.flatMap(line => line.segments.map((segment, index) => segment.length > 1 ? (
                    <polyline key={`${line.key}-${index}`} data-load-line={line.key} fill="none" stroke={line.color} strokeWidth={1.5} vectorEffect="non-scaling-stroke"
                        strokeLinejoin="round" points={segment.map(point => `${x(point.at)},${y(point.value)}`).join(" ")} />
                ) : (
                    <circle key={`${line.key}-${index}`} data-load-line={line.key} cx={x(segment[0].at)} cy={y(segment[0].value)} r={1.5} fill={line.color} />
                )))}
            </svg>
        </figure>
    );
}
