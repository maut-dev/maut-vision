import { nativeBridge } from "../../transport/native-bridge";
import type { CreateVariantRequestDto } from "./request-dto/create-variant-request-dto";
import type { DeleteVariantRequestDto } from "./request-dto/delete-variant-request-dto";
import type { ReviewVariantDeletionRequestDto } from "./request-dto/review-variant-deletion-request-dto";
import type { SetVariantArchivedRequestDto } from "./request-dto/set-variant-archived-request-dto";
import type { UpdateVariantRequestDto } from "./request-dto/update-variant-request-dto";
import { isDeletionResultResponseDto, isDeletionReviewResponseDto } from "./response-dto/deletion-review-response-dto";
import { isVariantResponseDto } from "./response-dto/variant-response-dto";

/**
 * Variants of the open project. The list of variants comes in the classes.getWorkspace snapshot; changes come with
 * the catalog.changed event. Errors: variants.project_not_open, variants.name_invalid, variants.description_invalid,
 * variants.name_duplicate, variants.not_found, variants.revision_conflict, variants.archived (editing an archived variant),
 * variants.deletion_revision_conflict,
 * variants.deletion_blocked.
 */
export const variantsApi = {
    get available(): boolean { return nativeBridge.available; },
    create(request: CreateVariantRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("variants.create", request, isVariantResponseDto, signal);
    },
    update(request: UpdateVariantRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("variants.update", request, isVariantResponseDto, signal);
    },
    setArchived(request: SetVariantArchivedRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("variants.setArchived", request, isVariantResponseDto, signal);
    },
    reviewDeletion(request: ReviewVariantDeletionRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("variants.reviewDeletion", request, isDeletionReviewResponseDto, signal);
    },
    delete(request: DeleteVariantRequestDto, signal?: AbortSignal) {
        return nativeBridge.request("variants.delete", request, isDeletionResultResponseDto, signal);
    },
};
