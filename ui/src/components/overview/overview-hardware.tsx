import { useEffect, useState } from "react";
import type { GpuSampleDto, HardwareSampleResponseDto } from "@/api/hardware/response-dto/hardware-sample-response-dto";
import { seriesColor } from "@/lib/checks/check-view";
import { appendLoad, loadSegments, loadWindowMs, type LoadPoint } from "@/lib/overview/load-history";
import { useHardwareSample } from "@/lib/use-hardware-sample";
import { locale } from "@/lib/format";
import { errorText } from "@/lib/errors";

const gigabytes = (bytes: number) => `${(bytes / 1024 ** 3).toLocaleString(locale(), { maximumFractionDigits: 2 })} ГБ`;

const percent = (value: number | null | undefined) =>
    typeof value === "number" ? `${value.toLocaleString(locale(), { maximumFractionDigits: 1 })}%` : "Недоступно";

const time = (value: string) => new Date(value).toLocaleTimeString(locale());

/** Цвет линии мини-графика: CPU — первая серия, видеокарта i — следующая за ней. */
const gpuColor = (index: number) => seriesColor(index + 2);

/**
 * «Ваш компьютер»: CPU, RAM, память приложения с разбивкой, видеокарты (нагрузка, память, температура, драйвер) и мини-график нагрузки
 * CPU и GPU за последние ~2 минуты. Снимок — useHardwareSample (раз в ~2 с, пока раздел открыт и окно видно); история графика живёт
 * в странице, пока раздел открыт.
 */
export function OverviewHardware() {
    const { sample, failed } = useHardwareSample();
    const [history, setHistory] = useState<readonly LoadPoint[]>([]);

    useEffect(() => {
        if (sample) setHistory(current => appendLoad(current, sample));
    }, [sample]);

    return (
        <section className="min-w-0 space-y-4 rounded-lg border bg-card p-4" aria-labelledby="overview-hardware-heading" data-testid="overview-hardware">
            <h2 id="overview-hardware-heading" className="font-medium">Ваш компьютер</h2>
            {failed && (
                <p role="alert" className="text-sm text-destructive" data-testid="overview-hardware-error">
                    Не удалось обновить показатели компьютера. Показатели могут быть устаревшими.
                </p>
            )}
            {!sample ? (
                <p role="status" className="text-sm text-muted-foreground">Сбор характеристик и нагрузки…</p>
            ) : (
                <HardwareSample sample={sample} history={history} />
            )}
        </section>
    );
}

function HardwareSample({ sample, history }: { sample: HardwareSampleResponseDto; history: readonly LoadPoint[] }) {
    const { cpu, ram, application } = sample;
    return (
        <>
            <div className="space-y-3" data-testid="overview-cpu" data-status={cpu.status}>
                <div>
                    <h3 className="break-words text-sm font-medium">{cpu.model ?? "Процессор"}</h3>
                    <p className="text-xs text-muted-foreground" data-testid="overview-cpu-threads">
                        {cpu.physicalCores !== null && `${cpu.physicalCores} ядер · `}{cpu.logicalThreads} потоков
                    </p>
                </div>
                <Usage label="CPU · система" value={cpu.utilizationPercent} detail={cpu.status === "available" || !cpu.reason ? undefined : errorText(cpu.reason)} testId="overview-cpu-load" />
            </div>
            <div className="space-y-2 border-t pt-4">
                {ram ? (
                    <Usage label="RAM · система" value={ram.utilizationPercent} detail={`${gigabytes(ram.usedBytes)} занято из ${gigabytes(ram.totalBytes)}`} testId="overview-ram" />
                ) : (
                    <Usage label="RAM · система" value={null} testId="overview-ram" />
                )}
                <div className="text-xs text-muted-foreground" data-testid="overview-application">
                    <p className="text-sm text-foreground">Приложение: {gigabytes(application.totalBytes)}</p>
                    <p data-testid="overview-application-parts">
                        Основной процесс {gigabytes(application.hostBytes)} · WebView2 ({application.webViewProcesses}) {gigabytes(application.webViewBytes)}
                        {" · "}Обучение и проверки ({application.workerProcesses}) {gigabytes(application.workerBytes)}
                    </p>
                </div>
            </div>
            <div className="space-y-4 border-t pt-4" data-testid="overview-gpus" data-status={sample.gpuStatus}>
                {sample.gpuStatus === "unavailable" || !sample.gpus.length ? (
                    <div>
                        <h3 className="text-sm font-medium">GPU</h3>
                        <p className="mt-2 text-xs text-muted-foreground" data-testid="overview-gpu-reason">{sample.gpuReason ? errorText(sample.gpuReason) : "Недоступно"}</p>
                    </div>
                ) : sample.gpus.map(gpu => <Gpu key={gpu.index} gpu={gpu} />)}
            </div>
            <LoadChart history={history} gpus={sample.gpus} />
            <p className="text-xs text-muted-foreground" data-testid="overview-hardware-time">Замер: {time(sample.timestamp)}</p>
        </>
    );
}

function Gpu({ gpu }: { gpu: GpuSampleDto }) {
    const memory = gpu.memoryTotalBytes && gpu.memoryUsedBytes !== null ? (gpu.memoryUsedBytes / gpu.memoryTotalBytes) * 100 : null;
    return (
        <div className="space-y-3" data-testid="overview-gpu" data-gpu-index={gpu.index}>
            <h3 className="break-words text-sm font-medium">GPU {gpu.index} · {gpu.name}</h3>
            <Usage label="Нагрузка" value={gpu.utilizationPercent} />
            <Usage
                label="Память"
                value={memory}
                detail={gpu.memoryTotalBytes !== null && gpu.memoryUsedBytes !== null ? `${gigabytes(gpu.memoryUsedBytes)} занято из ${gigabytes(gpu.memoryTotalBytes)}` : undefined}
            />
            <p className="text-xs text-muted-foreground" data-testid="overview-gpu-details">
                Температура {gpu.temperatureC === null ? "Недоступно" : `${gpu.temperatureC.toLocaleString(locale(), { maximumFractionDigits: 0 })} °C`}
                {" · "}Драйвер {gpu.driverVersion ?? "Недоступно"}
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

/** Мини-график нагрузки CPU и видеокарт за последние ~2 минуты: ось времени — окно до последнего замера, ось Y — 0–100 %. */
function LoadChart({ history, gpus }: { history: readonly LoadPoint[]; gpus: readonly GpuSampleDto[] }) {
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
                <span>Нагрузка за 2 минуты</span>
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
                aria-label="Нагрузка CPU и GPU за последние 2 минуты">
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
