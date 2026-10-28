import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { qualityMetricHelp, qualityMetricLabels, qualityMetrics, type QualityMetric } from "@/lib/checks/check-view";
import { formatDate } from "@/lib/format";
import { formatBytes, formatMetric } from "@/lib/training/run-view";

const weightKinds = [["weights_pt", "PT"], ["onnx", "ONNX"]] as const;

/** «Эпох X из Y (ранняя остановка)». */
export function epochsText(version: ModelVersionResponseDto): string {
    const requested = typeof version.parameters.epochs === "number" ? version.parameters.epochs : version.epochsCompleted;
    return `Эпох ${version.epochsCompleted} из ${requested}${version.stoppedEarly ? " (ранняя остановка)" : ""}`;
}

/** «Веса: PT 6,2 МБ, ONNX 12,1 МБ». */
export function weightsText(version: ModelVersionResponseDto): string | null {
    const weights = weightKinds.flatMap(([kind, label]) => version.files.filter(file => file.kind === kind).map(file => `${label} ${formatBytes(file.byteSize)}`));
    return weights.length ? `Веса: ${weights.join(", ")}` : null;
}

/**
 * Карточка версии в «Моделях»: номер и «Рабочая», дата · датасет · архитектура · вход, эпохи и веса, действия и четыре метрики;
 * лучшая среди версий помечена «лучшая».
 */
export function ModelVersionCard({ version, datasetName, best, busy, setWorking, exportZip, openPassport, remove }: {
    version: ModelVersionResponseDto;
    datasetName: string;
    best: Record<QualityMetric, number | null>;
    busy: boolean;
    setWorking: () => void;
    exportZip: () => void;
    openPassport: () => void;
    remove: () => void;
}) {
    const weights = weightsText(version);
    return (
        <li className={`space-y-4 rounded-lg border bg-card p-5 ${version.isWorking ? "border-brand/50" : ""}`} data-testid="model-version-card"
            data-version-id={version.id} data-working={version.isWorking || undefined}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <h3 className="flex items-center gap-2 font-medium">
                        Версия {version.number}
                        {version.isWorking && <Badge data-testid="model-version-card-working">Рабочая</Badge>}
                    </h3>
                    <p className="mt-1 text-xs text-muted-foreground" data-testid="model-version-card-meta">
                        {formatDate(version.createdAt)} · {datasetName} · {version.architecture} · {version.input.width} × {version.input.height}
                    </p>
                    <p className="mt-0.5 text-xs text-muted-foreground" data-testid="model-version-card-weights">
                        {epochsText(version)}{weights && ` · ${weights}`}
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {!version.isWorking && (
                        <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-card-set-working" onClick={setWorking}>Сделать рабочей</Button>
                    )}
                    <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-card-export" onClick={exportZip}>Выгрузить ZIP</Button>
                    <Button size="sm" data-testid="model-version-card-passport" onClick={openPassport}>Паспорт</Button>
                    <Button variant="ghost" size="sm" disabled={busy} data-testid="model-version-card-delete" onClick={remove}>Удалить</Button>
                </div>
            </div>
            <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4">
                {qualityMetrics.map(key => {
                    const value = version.metrics[key];
                    const top = typeof value === "number" && best[key] !== null && value === best[key];
                    return (
                        <div key={key} className="rounded-md border px-3 py-2" title={qualityMetricHelp[key]} data-metric={key} data-best={top || undefined}>
                            <dt className="text-xs text-muted-foreground">{qualityMetricLabels[key]}</dt>
                            <dd className={`mt-0.5 tabular-nums ${top ? "font-semibold text-[var(--metric-best)]" : ""}`}>
                                {formatMetric(value)}
                                {top && <span className="ml-1.5 text-xs font-normal text-muted-foreground">лучшая</span>}
                            </dd>
                        </div>
                    );
                })}
            </dl>
        </li>
    );
}
