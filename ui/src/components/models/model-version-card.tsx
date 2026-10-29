import { useTranslation } from "react-i18next";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { qualityMetricHelp, qualityMetricLabels, qualityMetrics, type QualityMetric } from "@/lib/checks/check-view";
import { formatDate, formatMetric } from "@/lib/format";
import { i18n } from "@/i18n";
import { formatBytes } from "@/lib/training/run-view";

const weightKinds = [["weights_pt", "PT"], ["onnx", "ONNX"]] as const;

/** "Epochs X of Y (early stop)". */
export function epochsText(version: ModelVersionResponseDto): string {
    const requested = typeof version.parameters.epochs === "number" ? version.parameters.epochs : version.epochsCompleted;
    return i18n.t(version.stoppedEarly ? "models:card.epochsStopped" : "models:card.epochs", { done: version.epochsCompleted, total: requested });
}

/** "Weights: PT 6.2 MB, ONNX 12.1 MB". */
export function weightsText(version: ModelVersionResponseDto): string | null {
    const weights = weightKinds.flatMap(([kind, label]) => version.files.filter(file => file.kind === kind).map(file => `${label} ${formatBytes(file.byteSize)}`));
    return weights.length ? i18n.t("models:card.weights", { list: weights.join(", ") }) : null;
}

/**
 * A version card in "Models": the number and "Working", date · dataset · architecture · input, epochs and weights, actions and four
 * metrics; the best among the versions is marked "best".
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
    const { t } = useTranslation("models");
    const weights = weightsText(version);
    return (
        <li className={`space-y-4 rounded-lg border bg-card p-5 ${version.isWorking ? "border-brand/50" : ""}`} data-testid="model-version-card"
            data-version-id={version.id} data-working={version.isWorking || undefined}>
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                    <h3 className="flex items-center gap-2 font-medium">
                        {t("card.version", { number: version.number })}
                        {version.isWorking && <Badge data-testid="model-version-card-working">{t("card.working")}</Badge>}
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
                        <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-card-set-working" onClick={setWorking}>{t("card.makeWorking")}</Button>
                    )}
                    <Button variant="outline" size="sm" disabled={busy} data-testid="model-version-card-export" onClick={exportZip}>{t("card.exportZip")}</Button>
                    <Button size="sm" data-testid="model-version-card-passport" onClick={openPassport}>{t("card.passport")}</Button>
                    <Button variant="ghost" size="sm" disabled={busy} data-testid="model-version-card-delete" onClick={remove}>{t("card.delete")}</Button>
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
                                {top && <span className="ml-1.5 text-xs font-normal text-muted-foreground">{t("card.best")}</span>}
                            </dd>
                        </div>
                    );
                })}
            </dl>
        </li>
    );
}
