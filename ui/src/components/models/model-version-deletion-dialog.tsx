import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { ModelVersionDeletionResponseDto, ModelVersionDeletionReviewResponseDto } from "@/api/projects/training/response-dto/model-version-deletion-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { BridgeError } from "@/api/transport/errors";
import { ReviewDeletionDialog } from "@/components/deletion/review-deletion-dialog";
import { i18n } from "@/i18n";
import { formatBytes } from "@/lib/training/run-view";
import "./locale";

/** Reasons of the refusal training.version_in_use (details.reason) — the same text as the blocker in the review (read at render time). */
export const versionInUseReasons: Readonly<Record<string, string>> = {
    get working() { return i18n.t("models:inUse.working"); },
    get evaluation() { return i18n.t("models:inUse.evaluation"); },
    get check() { return i18n.t("models:inUse.check"); },
    get export() { return i18n.t("models:inUse.export"); },
};

/**
 * "Delete" a model version: the shared deletion dialog — what goes (evaluations on datasets are deleted with it, the file size) and the
 * blockers (working, under a check, exporting). A refusal on deletion (the state changed after the review) names the reason.
 */
export function ModelVersionDeletionDialog({ projectId, versionId, number, close, deleted }: {
    projectId: string;
    versionId: string;
    number: number;
    close: () => void;
    deleted: (result: ModelVersionDeletionResponseDto) => void;
}) {
    const { t } = useTranslation("models");
    const review = useCallback((signal: AbortSignal) => trainingApi.reviewModelVersionDeletion({ projectId, versionId }, signal), [projectId, versionId]);

    async function remove(): Promise<ModelVersionDeletionResponseDto> {
        try {
            return await trainingApi.deleteModelVersion({ projectId, versionId });
        } catch (cause) {
            const reason = cause instanceof BridgeError && cause.code === "training.version_in_use" ? cause.details?.reason : undefined;
            if (reason && Object.hasOwn(versionInUseReasons, reason)) throw new Error(t("deletion.refused", { reason: versionInUseReasons[reason] }));
            throw cause;
        }
    }

    return (
        <ReviewDeletionDialog<ModelVersionDeletionReviewResponseDto, ModelVersionDeletionResponseDto>
            area="training"
            title={t("deletion.title", { number })}
            description={t("deletion.description")}
            testId="model-version-deletion-dialog"
            review={review}
            remove={remove}
            deleted={deleted}
            close={close}
            busyText={t("deletion.busy")}
            blockersTitle={t("deletion.blockers")}
            renderReview={item => (
                <ul className="space-y-1" data-testid="model-version-deletion-review">
                    <li>{t("deletion.files", { size: formatBytes(item.bytes) })}</li>
                    <li>{t(item.evaluations > 0 ? "deletion.evaluationsDeleted" : "deletion.evaluations", { number: item.evaluations })}</li>
                </ul>
            )}
        />
    );
}
