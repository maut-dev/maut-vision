import { useTranslation } from "react-i18next";
import { CheckCircle2, Loader2, XCircle } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { i18n } from "@/i18n";
import { jobErrorText } from "@/lib/errors";
import { formatBytes, formatNumber } from "@/lib/format";
import { isTransferRunning, type ProjectTransfer } from "@/lib/use-project-transfers";


type TransferState = "running" | "succeeded" | "cancelled" | "failed";

function transferState(transfer: ProjectTransfer): TransferState {
    if (isTransferRunning(transfer)) return "running";
    const status = transfer.kind === "export" ? transfer.job.status : transfer.operation.status;
    return status === "succeeded" || status === "cancelled" ? status : "failed";
}

/** Заголовок: «Экспорт проекта «X»…», «Импорт проекта…», «Проект «X» импортирован», «Импорт отменён», «Экспорт не выполнен»… */
export function transferTitle(transfer: ProjectTransfer): string {
    const state = transferState(transfer);
    const exporting = transfer.kind === "export";
    const cancelRequested = exporting ? transfer.job.cancelRequested : transfer.operation.cancelRequested;
    const name = exporting ? transfer.projectName : transfer.operation.projectName ?? "";
    if (state === "running") return cancelRequested ? i18n.t("transfer.rollingBack") : exporting ? i18n.t("transfer.exporting", { name }) : i18n.t("transfer.importing");
    if (state === "succeeded") return exporting ? i18n.t("transfer.exported", { name }) : i18n.t("transfer.imported", { name });
    if (state === "cancelled") return exporting ? i18n.t("transfer.exportCancelled") : i18n.t("transfer.importCancelled");
    return exporting ? i18n.t("transfer.exportFailed") : i18n.t("transfer.importFailed");
}

/** Ход: «Файлов: N из M.» (до подсчёта — подготовка), итог или текст отказа. */
export function transferDetail(transfer: ProjectTransfer): string {
    const state = transferState(transfer);
    if (state === "running") {
        const [processed, total] = transfer.kind === "export"
            ? [transfer.job.processed ?? 0, transfer.job.total ?? 0]
            : [transfer.operation.processedFiles, transfer.operation.totalFiles];
        return total ? i18n.t("transfer.files", { processed: formatNumber(processed), total: formatNumber(total) })
            : transfer.kind === "export" ? i18n.t("transfer.preparing") : i18n.t("transfer.checkingArchive");
    }
    if (state === "succeeded") {
        return transfer.kind === "export" ? i18n.t("transfer.exportDone") : i18n.t("transfer.importDone");
    }
    if (state === "cancelled") return i18n.t("transfer.cancelled");
    const error = transfer.kind === "export" ? transfer.job.error : transfer.operation.error;
    if (error) return jobErrorText(error);
    return transfer.kind === "export" ? i18n.t("transfer.exportInterrupted") : i18n.t("transfer.importInterrupted");
}

/** Байты: у импорта — распаковано из общего объёма, у экспорта — размер архива после итога. */
function transferBytes(transfer: ProjectTransfer): string | null {
    if (transfer.kind === "import") {
        const { processedBytes, totalBytes } = transfer.operation;
        return totalBytes && transferState(transfer) === "running"
            ? i18n.t("transfer.bytes", { processed: formatBytes(processedBytes), total: formatBytes(totalBytes) }) : null;
    }
    if (transfer.result && transfer.job.status === "succeeded") {
        return `${transfer.fileName ?? transfer.result.fileName} · ${formatBytes(transfer.result.archiveBytes)}`;
    }
    return null;
}

function transferPercent(transfer: ProjectTransfer): number | null {
    if (transfer.kind === "export") {
        const { job } = transfer;
        if (job.progress !== null) return job.progress;
        return job.total ? ((job.processed ?? 0) / job.total) * 100 : null;
    }
    const { processedBytes, totalBytes } = transfer.operation;
    return totalBytes ? (processedBytes / totalBytes) * 100 : null;
}

/** Баннер переноса в библиотеке: ход с «Отменить» или итог со «Скрыть». */
export function ProjectTransferBanner({ transfer, cancel, dismiss }: {
    transfer: ProjectTransfer;
    cancel: () => void;
    dismiss: () => void;
}) {
    const { t } = useTranslation();
    const state = transferState(transfer);
    const running = state === "running";
    const cancelRequested = transfer.kind === "export" ? transfer.job.cancelRequested : transfer.operation.cancelRequested;
    const percent = running ? transferPercent(transfer) : null;
    const bytes = transferBytes(transfer);
    const title = transferTitle(transfer);
    return (
        <Alert variant={state === "failed" ? "destructive" : "default"} role={state === "failed" ? "alert" : "status"} data-testid="transfer-banner" data-kind={transfer.kind} data-status={state}>
            {running ? <Loader2 className="animate-spin" aria-hidden /> : state === "succeeded" ? <CheckCircle2 aria-hidden /> : <XCircle aria-hidden />}
            <AlertTitle className="break-words" data-testid="transfer-title">{title}</AlertTitle>
            <AlertDescription>
                <p data-testid="transfer-detail">{transferDetail(transfer)}</p>
                {bytes && <p className="tabular-nums text-muted-foreground" data-testid="transfer-bytes">{bytes}</p>}
                {running && <p className="text-muted-foreground" data-testid="transfer-hint">{t("transfer.hint")}</p>}
                {running && (
                    <div className="mt-1 h-1 w-full overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("transfer.progress", { title })}
                        aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                        <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                            style={percent === null ? undefined : { width: `${Math.min(100, percent)}%` }} />
                    </div>
                )}
                <div className="mt-2 flex gap-2">
                    {running
                        ? <Button size="sm" variant="outline" disabled={cancelRequested} onClick={cancel} data-testid="transfer-cancel">{t("common.cancelAction")}</Button>
                        : <Button size="sm" variant="ghost" onClick={dismiss} data-testid="transfer-dismiss">{t("common.hide")}</Button>}
                </div>
            </AlertDescription>
        </Alert>
    );
}
