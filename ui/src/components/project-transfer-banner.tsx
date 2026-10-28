import { CheckCircle2, Loader2, XCircle } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { formatBytes } from "@/lib/training/run-view";
import { isTransferRunning, type ProjectTransfer } from "@/lib/use-project-transfers";

const count = (value: number) => value.toLocaleString("ru-RU");

export const transferHint = "Большой проект может переноситься несколько минут; работать с другими проектами можно.";
export const transferCancelled = "Операция отменена. Изменений нет.";

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
    if (state === "running") return cancelRequested ? "Отмена: откатываем изменения…" : exporting ? `Экспорт проекта «${name}»…` : "Импорт проекта…";
    if (state === "succeeded") return exporting ? `Проект «${name}» экспортирован` : `Проект «${name}» импортирован`;
    if (state === "cancelled") return exporting ? "Экспорт отменён" : "Импорт отменён";
    return exporting ? "Экспорт не выполнен" : "Импорт не выполнен";
}

/** Ход: «Файлов: N из M.» (до подсчёта — подготовка), итог или текст отказа. */
export function transferDetail(transfer: ProjectTransfer): string {
    const state = transferState(transfer);
    if (state === "running") {
        const [processed, total] = transfer.kind === "export"
            ? [transfer.job.processed ?? 0, transfer.job.total ?? 0]
            : [transfer.operation.processedFiles, transfer.operation.totalFiles];
        return total ? `Файлов: ${count(processed)} из ${count(total)}.` : transfer.kind === "export" ? "Подготовка данных." : "Проверка архива.";
    }
    if (state === "succeeded") {
        return transfer.kind === "export"
            ? "Архив сохранён. Перенесите файл на другой компьютер и выберите «Импортировать проект…»."
            : "Проект добавлен в библиотеку.";
    }
    if (state === "cancelled") return transferCancelled;
    const error = transfer.kind === "export" ? transfer.job.error : transfer.operation.error;
    return error?.message ?? (transfer.kind === "export" ? "Экспорт прерван. Повторите экспорт." : "Импорт прерван. Повторите импорт.");
}

/** Байты: у импорта — распаковано из общего объёма, у экспорта — размер архива после итога. */
function transferBytes(transfer: ProjectTransfer): string | null {
    if (transfer.kind === "import") {
        const { processedBytes, totalBytes } = transfer.operation;
        return totalBytes && transferState(transfer) === "running" ? `${formatBytes(processedBytes)} из ${formatBytes(totalBytes)}` : null;
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
                {running && <p className="text-muted-foreground" data-testid="transfer-hint">{transferHint}</p>}
                {running && (
                    <div className="mt-1 h-1 w-full overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={`Ход: ${title}`}
                        aria-valuemin={0} aria-valuemax={100} aria-valuenow={percent === null ? undefined : Math.round(percent)}>
                        <div className={percent === null ? "h-full w-1/3 animate-pulse bg-brand" : "h-full bg-brand transition-[width]"}
                            style={percent === null ? undefined : { width: `${Math.min(100, percent)}%` }} />
                    </div>
                )}
                <div className="mt-2 flex gap-2">
                    {running
                        ? <Button size="sm" variant="outline" disabled={cancelRequested} onClick={cancel} data-testid="transfer-cancel">Отменить</Button>
                        : <Button size="sm" variant="ghost" onClick={dismiss} data-testid="transfer-dismiss">Скрыть</Button>}
                </div>
            </AlertDescription>
        </Alert>
    );
}
