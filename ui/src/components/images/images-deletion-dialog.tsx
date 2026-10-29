import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageDeletionResultResponseDto, ImageDeletionReviewResponseDto } from "@/api/projects/images/response-dto/image-deletion-response-dto";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/images";

/** The whole group (no imageIds) or selected images of one group (up to 400). */
export interface ImagesDeletionTarget {
    groupId: string;
    name: string;
    imageIds?: readonly string[];
}

/**
 * Permanent deletion in two phases: a review of the contents and blockers (an import into this group), then deletion with the
 * revision of the review. Rows are deleted at once, files and thumbnails in the background. A failed deletion needs "Check again".
 */
export function ImagesDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: ImagesDeletionTarget;
    close: () => void;
    deleted: (result: ImageDeletionResultResponseDto) => void;
}) {
    const { t } = useTranslation("images");
    const selection = !!target.imageIds;
    const review = useCallback((signal: AbortSignal): Promise<ImageDeletionReviewResponseDto> => target.imageIds
        ? imagesApi.reviewDeletion({ projectId, groupId: target.groupId, imageIds: [...target.imageIds] }, signal)
        : imagesApi.reviewGroupDeletion({ projectId, groupId: target.groupId }, signal), [projectId, target]);
    const remove = (current: ImageDeletionReviewResponseDto) => target.imageIds
        ? imagesApi.delete({ projectId, groupId: target.groupId, imageIds: [...target.imageIds], reviewRevision: current.revision })
        : imagesApi.deleteGroup({ projectId, groupId: target.groupId, reviewRevision: current.revision });

    return (
        <ReviewDeletionDialog
            area="images"
            testId="images-deletion-dialog"
            title={selection ? t("deletion.imagesTitle") : t("deletion.groupTitle")}
            description={<>
                {selection ? t("deletion.imagesText", { name: target.name }) : t("deletion.groupText", { name: target.name })}
            </>}
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText={t("deletion.busy")}
            blockersTitle={<p className="font-medium">{t("deletion.blockers")}</p>}
            blockersNote={<p className="mt-2">{t("deletion.blockersNote")}</p>}
            renderReview={current => (
                <>
                    <p data-testid="images-deletion-count">{t("deletion.count", { number: formatNumber(current.images) })}</p>
                    <p className="text-muted-foreground">{t("deletion.kept")}</p>
                </>
            )}
        />
    );
}
