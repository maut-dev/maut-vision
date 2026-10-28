import type { TrainingEpochDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";

/** Эпох на странице listEpochs. */
const epochPageLimit = 1000;

/**
 * Вся история эпох запуска, страница за страницей по nextAfter. active() — владелец ещё ждёт ответа: после ухода следующие страницы
 * не запрашиваются, а результат — null. История версии неизменна после её рождения, поэтому читается один раз.
 */
export async function readEpochHistory(projectId: string, runId: string, active: () => boolean = () => true): Promise<TrainingEpochDto[] | null> {
    const items: TrainingEpochDto[] = [];
    let after: number | undefined;
    do {
        const page = await trainingApi.listEpochs({ projectId, runId, after, limit: epochPageLimit });
        if (!active()) return null;
        items.push(...page.items);
        if (page.nextAfter !== null && after !== undefined && page.nextAfter <= after) throw new Error("История эпох вернула повторную страницу.");
        after = page.nextAfter ?? undefined;
    } while (after !== undefined);
    return items;
}
