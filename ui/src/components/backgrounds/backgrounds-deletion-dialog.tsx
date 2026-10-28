import { useCallback } from "react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { BackgroundDeletionResultResponseDto, BackgroundDeletionReviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-deletion-response-dto";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";

/** Весь набор (itemIds не задан) или выбранные фоны одного набора (до 400). */
export interface BackgroundsDeletionTarget {
    setId: string;
    name: string;
    itemIds?: readonly string[];
}

/**
 * «Удалить набор фонов навсегда?» / «Удалить фоны навсегда?»: просмотр состава и блокировок (подготовка в этот набор),
 * затем удаление с ревизией просмотра. Задачи других наборов удаление не блокируют.
 */
export function BackgroundsDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: BackgroundsDeletionTarget;
    close: () => void;
    deleted: (result: BackgroundDeletionResultResponseDto) => void;
}) {
    const selection = !!target.itemIds;
    const review = useCallback((signal: AbortSignal): Promise<BackgroundDeletionReviewResponseDto> => target.itemIds
        ? backgroundsApi.reviewDeletion({ projectId, setId: target.setId, itemIds: [...target.itemIds] }, signal)
        : backgroundsApi.reviewSetDeletion({ projectId, setId: target.setId }, signal), [projectId, target]);
    const remove = (current: BackgroundDeletionReviewResponseDto) => target.itemIds
        ? backgroundsApi.delete({ projectId, setId: target.setId, itemIds: [...target.itemIds], reviewRevision: current.revision })
        : backgroundsApi.deleteSet({ projectId, setId: target.setId, reviewRevision: current.revision });

    return (
        <ReviewDeletionDialog
            testId="backgrounds-deletion-dialog"
            title={selection ? "Удалить фоны навсегда?" : "Удалить набор фонов навсегда?"}
            description={target.name}
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText="Удаление…"
            blockersTitle={<p>Завершите задачи или измените зависимости перед удалением:</p>}
            renderReview={current => (
                <>
                    <p data-testid="backgrounds-deletion-count">Будет удалено фонов: <span className="tabular-nums">{current.items.toLocaleString("ru-RU")}</span>.</p>
                    <p className="text-muted-foreground">
                        Исходные файлы и подготовленные изображения будут удалены с компьютера. Это действие нельзя отменить.
                        {!selection && " История подготовки этого набора также будет удалена."} Готовые датасеты и модели сохранят свои материалы.
                    </p>
                </>
            )}
        />
    );
}
