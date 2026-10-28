import { useMemo, useState, type PointerEvent } from "react";
import { chartDomain, epochTicks, nearestEpoch, seriesEpochs, thinPoints, ticks, type EpochSeries } from "@/lib/training/epoch-chart";
import { formatNumber } from "@/lib/training/run-view";

const width = 640;
const height = 240;
const pad = { left: 44, right: 12, top: 12, bottom: 26 };
const plotWidth = width - pad.left - pad.right;
const plotHeight = height - pad.top - pad.bottom;
/** Не больше двух точек на пиксель ширины: длинные прогоны прореживаются, подсказка берёт значения из полной серии. */
const maxDrawnPoints = plotWidth * 2;

/**
 * Серии по эпохам на одной оси Y (SVG без зависимостей): легенда с переключением серий, перекрестие и
 * подсказка «Эпоха N». unit — ось 0..1 (качество), иначе от нуля до максимума (потери). Окна в 200 эпох нет: весь прогон
 * рисуется целиком, длинные серии прореживаются с сохранением пиков.
 */
export function TrainingEpochChart({ id, label, series, unit }: { id: string; label: string; series: readonly EpochSeries[]; unit: boolean }) {
    const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set());
    const [hover, setHover] = useState<number | null>(null);
    const shown = series.filter(line => !hidden.has(line.key));
    const domain = chartDomain(shown.length ? shown : series, unit);
    const epochs = useMemo(() => seriesEpochs(series), [series]);
    const lookup = useMemo(() => new Map(series.map(line => [line.key, new Map(line.points.map(point => [point.epoch, point.value]))])), [series]);
    const drawn = useMemo(() => new Map(series.map(line => [line.key, thinPoints(line.points, maxDrawnPoints)])), [series]);
    const { minEpoch, maxEpoch, yMin, yMax } = domain;
    const x = (epoch: number) => pad.left + (maxEpoch === minEpoch ? plotWidth / 2 : ((epoch - minEpoch) / (maxEpoch - minEpoch)) * plotWidth);
    const y = (value: number) => pad.top + plotHeight - ((value - yMin) / (yMax - yMin || 1)) * plotHeight;
    const drawnCount = shown.reduce((sum, line) => sum + (drawn.get(line.key)?.length ?? 0), 0);

    function pointer(event: PointerEvent<SVGSVGElement>) {
        const box = event.currentTarget.getBoundingClientRect();
        const svgX = ((event.clientX - box.left) / box.width) * width;
        const ratio = (svgX - pad.left) / plotWidth;
        if (ratio < -0.02 || ratio > 1.02) {
            setHover(null);
            return;
        }
        setHover(nearestEpoch(epochs, minEpoch + ratio * (maxEpoch - minEpoch)));
    }

    function toggle(key: string) {
        setHidden(current => {
            const next = new Set(current);
            if (next.has(key)) next.delete(key);
            else next.add(key);
            return next;
        });
    }

    return (
        <figure className="space-y-2" data-testid="training-epoch-chart" data-chart={id} data-epochs={epochs.length} data-drawn-points={drawnCount}>
            <figcaption className="text-sm font-medium">{label}</figcaption>
            <ul className="flex flex-wrap gap-x-3 gap-y-1 text-xs" aria-label={`Легенда: ${label}`}>
                {series.map(line => {
                    const off = hidden.has(line.key);
                    return (
                        <li key={line.key}>
                            <button
                                type="button"
                                aria-pressed={!off}
                                data-series={line.key}
                                title={off ? "Показать серию" : "Скрыть серию"}
                                onClick={() => toggle(line.key)}
                                className={`flex items-center gap-1.5 rounded px-1 py-0.5 transition-colors hover:bg-muted/60 ${off ? "opacity-40" : ""}`}
                            >
                                <svg width="18" height="4" aria-hidden="true">
                                    <line x1="0" x2="18" y1="2" y2="2" stroke={line.color} strokeWidth="2" strokeDasharray={line.dashed ? "4 3" : undefined} />
                                </svg>
                                <span className="text-foreground/80">{line.label}</span>
                            </button>
                        </li>
                    );
                })}
            </ul>
            <div className="relative">
                <svg
                    viewBox={`0 0 ${width} ${height}`}
                    className="h-auto w-full touch-none select-none"
                    role="img"
                    aria-label={label}
                    onPointerMove={pointer}
                    onPointerLeave={() => setHover(null)}
                >
                    {ticks(yMin, yMax, 4).map(tick => (
                        <g key={tick}>
                            <line x1={pad.left} x2={width - pad.right} y1={y(tick)} y2={y(tick)} stroke="var(--border)" strokeDasharray="3 3" />
                            <text x={pad.left - 6} y={y(tick)} textAnchor="end" dominantBaseline="middle" fontSize="13" fill="var(--muted-foreground)">
                                {unit ? tick.toFixed(2) : formatNumber(tick, 2)}
                            </text>
                        </g>
                    ))}
                    {epochTicks(minEpoch, maxEpoch).map(tick => (
                        <text key={tick} x={x(tick)} y={height - 8} textAnchor="middle" fontSize="13" fill="var(--muted-foreground)">{tick}</text>
                    ))}
                    {hover !== null && (
                        <line x1={x(hover)} x2={x(hover)} y1={pad.top} y2={pad.top + plotHeight} stroke="var(--foreground)" strokeOpacity={0.35} data-testid="training-chart-crosshair" />
                    )}
                    {shown.map(line => {
                        const points = drawn.get(line.key) ?? [];
                        return points.length > 1 ? (
                            <polyline
                                key={line.key}
                                data-series-line={line.key}
                                fill="none"
                                stroke={line.color}
                                strokeWidth={2}
                                strokeDasharray={line.dashed ? "6 4" : undefined}
                                strokeLinejoin="round"
                                strokeLinecap="round"
                                points={points.map(point => `${x(point.epoch)},${y(point.value)}`).join(" ")}
                            />
                        ) : points.length === 1 ? (
                            <circle key={line.key} data-series-line={line.key} cx={x(points[0].epoch)} cy={y(points[0].value)} r={4} fill={line.color} />
                        ) : null;
                    })}
                    {hover !== null && shown.map(line => {
                        const value = lookup.get(line.key)?.get(hover);
                        return value === undefined ? null : (
                            <circle key={line.key} cx={x(hover)} cy={y(value)} r={4} fill={line.color} stroke="var(--card)" strokeWidth={2} />
                        );
                    })}
                </svg>
                {hover !== null && (
                    <div className="pointer-events-none absolute right-2 top-2 rounded-md border bg-background/95 px-3 py-2 text-xs shadow" data-testid="training-chart-tooltip">
                        <p className="mb-1 text-muted-foreground">Эпоха {hover}</p>
                        {shown.map(line => {
                            const value = lookup.get(line.key)?.get(hover);
                            return (
                                <p key={line.key} className="flex items-center justify-between gap-4">
                                    <span className="flex items-center gap-1.5">
                                        <span className="inline-block size-2 rounded-full" style={{ background: line.color }} />
                                        {line.label}
                                    </span>
                                    <span className="tabular-nums">{value === undefined ? "—" : formatNumber(value, 4)}</span>
                                </p>
                            );
                        })}
                    </div>
                )}
            </div>
        </figure>
    );
}
