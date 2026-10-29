import { useCallback } from "react";
import { Trans, useTranslation } from "react-i18next";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { BackgroundDeletionResultResponseDto, BackgroundDeletionReviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-deletion-response-dto";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";
import { formatNumber } from "@/lib/format";
import "@/i18n/sections/backgrounds";

/** The whole set (no itemIds) or selected backgrounds of one set (up to 400). */
export interface BackgroundsDeletionTarget {
    setId: string;
    name: string;
    itemIds?: readonly string[];
}

/**
 * "Delete the background set permanently?" / "Delete the backgrounds permanently?": a review of the contents and blockers (preparation
 * into this set), then deletion with the revision of the review. Jobs of other sets do not block the deletion.
 */
export function BackgroundsDeletionDialog({ projectId, target, close, deleted }: {
    projectId: string;
    target: BackgroundsDeletionTarget;
    close: () => void;
    deleted: (result: BackgroundDeletionResultResponseDto) => void;
}) {
    const { t } = useTranslation("backgrounds");
    const selection = !!target.itemIds;
    const review = useCallback((signal: AbortSignal): Promise<BackgroundDeletionReviewResponseDto> => target.itemIds
        ? backgroundsApi.reviewDeletion({ projectId, setId: target.setId, itemIds: [...target.itemIds] }, signal)
        : backgroundsApi.reviewSetDeletion({ projectId, setId: target.setId }, signal), [projectId, target]);
    const remove = (current: BackgroundDeletionReviewResponseDto) => target.itemIds
        ? backgroundsApi.delete({ projectId, setId: target.setId, itemIds: [...target.itemIds], reviewRevision: current.revision })
        : backgroundsApi.deleteSet({ projectId, setId: target.setId, reviewRevision: current.revision });

    return (
        <ReviewDeletionDialog
            area="backgrounds"
            testId="backgrounds-deletion-dialog"
            title={selection ? t("deletion.itemsTitle") : t("deletion.setTitle")}
            description={target.name}
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText={t("deletion.busy")}
            blockersTitle={<p>{t("deletion.blockers")}</p>}
            renderReview={current => (
                <>
                    <p data-testid="backgrounds-deletion-count">
                        <Trans t={t} i18nKey="deletion.count" values={{ number: formatNumber(current.items) }} components={{ number: <span className="tabular-nums" /> }} />
                    </p>
                    <p className="text-muted-foreground">{selection ? t("deletion.itemsText") : t("deletion.setText")}</p>
                </>
            )}
        />
    );
}
