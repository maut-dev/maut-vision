import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { i18n } from "@/i18n";
import "./locale";

/** Epochs per page of listEpochs. */
const epochPageLimit = 1000;

/**
 * The whole epoch history of a run, page by page by nextAfter. active() — the owner still waits for the answer: after it leaves the next
 * pages are not requested and the result is null. The history of a version does not change after its birth, so it is read once.
 */
export async function readEpochHistory(projectId: string, runId: string, active: () => boolean = () => true): Promise<TrainingEpochDto[] | null> {
    const items: TrainingEpochDto[] = [];
    let after: number | undefined;
    do {
        const page = await trainingApi.listEpochs({ projectId, runId, after, limit: epochPageLimit });
        if (!active()) return null;
        items.push(...page.items);
        if (page.nextAfter !== null && after !== undefined && page.nextAfter <= after) throw new Error(i18n.t("training:loadErrors.epochPageRepeated"));
        after = page.nextAfter ?? undefined;
    } while (after !== undefined);
    return items;
}
