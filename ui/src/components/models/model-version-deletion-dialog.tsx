import { useCallback } from "react";
import type { ModelVersionDeletionResponseDto, ModelVersionDeletionReviewResponseDto } from "@/api/projects/training/response-dto/model-version-deletion-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { BridgeError } from "@/api/transport/errors";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";
import { formatBytes } from "@/lib/training/run-view";

/** Причина отказа training.version_in_use (details.reason) — тем же текстом, что блокировка в просмотре состава. */
export const versionInUseReasons: Readonly<Record<string, string>> = {
    working: "Это рабочая версия: сначала сделайте рабочей другую.",
    evaluation: "Идёт проверка этой версии на датасете.",
    check: "Идёт живая проверка этой версии.",
    export: "Идёт выгрузка этой версии в ZIP.",
};

/**
 * «Удалить» версию модели: общий диалог удаления — состав (оценки на датасетах удаляются вместе с ней, размер файлов) и причины
 * запрета (рабочая, проверяется, выгружается). Отказ при удалении (состояние изменилось после просмотра) называет причину.
 */
export function ModelVersionDeletionDialog({ projectId, versionId, number, close, deleted }: {
    projectId: string;
    versionId: string;
    number: number;
    close: () => void;
    deleted: (result: ModelVersionDeletionResponseDto) => void;
}) {
    const review = useCallback((signal: AbortSignal) => trainingApi.reviewModelVersionDeletion({ projectId, versionId }, signal), [projectId, versionId]);

    async function remove(): Promise<ModelVersionDeletionResponseDto> {
        try {
            return await trainingApi.deleteModelVersion({ projectId, versionId });
        } catch (cause) {
            const reason = cause instanceof BridgeError && cause.code === "training.version_in_use" ? cause.details?.reason : undefined;
            if (reason && versionInUseReasons[reason]) throw new Error(`Версию сейчас нельзя удалить. ${versionInUseReasons[reason]}`);
            throw cause;
        }
    }

    return (
        <ReviewDeletionDialog<ModelVersionDeletionReviewResponseDto, ModelVersionDeletionResponseDto>
            area="training"
            title={`Удалить версию ${number}?`}
            description="Веса, картинки обучения и проверки версии на датасетах удалятся навсегда. Запуск обучения в истории останется."
            testId="model-version-deletion-dialog"
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText="Удаление…"
            blockersTitle="Сейчас версию удалить нельзя:"
            renderReview={item => (
                <ul className="space-y-1" data-testid="model-version-deletion-review">
                    <li>Файлы версии: {formatBytes(item.bytes)}</li>
                    <li>Проверок на датасетах: {item.evaluations}{item.evaluations > 0 && " — удалятся вместе с версией"}</li>
                </ul>
            )}
        />
    );
}
