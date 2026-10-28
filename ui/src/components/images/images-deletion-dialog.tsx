import { useCallback } from "react";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageDeletionResultResponseDto, ImageDeletionReviewResponseDto } from "@/api/projects/images/response-dto/image-deletion-response-dto";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";

/** Вся группа (imageIds не задан) или выбранные изображения одной группы (до 400). */
export interface ImagesDeletionTarget {
    groupId: string;
    name: string;
    imageIds?: readonly string[];
}

/**
 * Удаление навсегда в две фазы: просмотр состава и блокировок (импорт в эту группу), затем удаление с ревизией
 * просмотра. Строки удаляются сразу, файлы и миниатюры — в фоне. Ошибка удаления требует «Проверить снова».
 */
export function ImagesDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: ImagesDeletionTarget;
    close: () => void;
    deleted: (result: ImageDeletionResultResponseDto) => void;
}) {
    const selection = !!target.imageIds;
    const review = useCallback((signal: AbortSignal): Promise<ImageDeletionReviewResponseDto> => target.imageIds
        ? imagesApi.reviewDeletion({ projectId, groupId: target.groupId, imageIds: [...target.imageIds] }, signal)
        : imagesApi.reviewGroupDeletion({ projectId, groupId: target.groupId }, signal), [projectId, target]);
    const remove = (current: ImageDeletionReviewResponseDto) => target.imageIds
        ? imagesApi.delete({ projectId, groupId: target.groupId, imageIds: [...target.imageIds], reviewRevision: current.revision })
        : imagesApi.deleteGroup({ projectId, groupId: target.groupId, reviewRevision: current.revision });

    return (
        <ReviewDeletionDialog
            testId="images-deletion-dialog"
            title={selection ? "Удалить изображения навсегда?" : "Удалить группу изображений навсегда?"}
            description={<>
                {target.name}. {selection ? "Выбранные изображения, их файлы и разметка будут удалены." : "Группа, все её изображения, разметка и история импорта будут удалены."} Восстановить их в приложении нельзя.
            </>}
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText="Удаляем…"
            blockersTitle={<p className="font-medium">Удаление пока недоступно:</p>}
            blockersNote={<p className="mt-2">Завершите задачи или измените указанные зависимости и проверьте снова.</p>}
            renderReview={current => (
                <>
                    <p data-testid="images-deletion-count">Изображений к удалению: {current.images.toLocaleString("ru-RU")}</p>
                    <p className="text-muted-foreground">Готовые датасеты и модели сохранят собственные копии. Материалы других групп сохранятся.</p>
                </>
            )}
        />
    );
}
