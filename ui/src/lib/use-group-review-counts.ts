import { useEffect, useRef, useState } from "react";
import { subscribeImagesChanged } from "@/api/events/subscriptions";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";

/** Группы крупнее не считаются постранично: доля проверенных для них станет известна из плана. */
export const reviewCountLimit = 2000;
const pageLimit = 200;

/** Проверено активных изображений группы; null — группа крупнее предела или счёт не удался. */
export type ReviewCount = { reviewed: number; active: number } | null;

/**
 * Доля проверенных кадров для выбора групп сборки. В DTO группы нет счётчика проверенных, поэтому для групп до 2000 активных
 * изображений он считается страницами images.list с reviewState = reviewed (по 200). Пересчёт — при изменении группы (ревизия
 * или счётчики) и после images.changed с изменёнными изображениями (разметка), не чаще раза в секунду; без опроса. enabled —
 * считать только пока форма видна.
 */
export function useGroupReviewCounts(projectId: string, groups: readonly ImageGroupResponseDto[], enabled: boolean) {
    const [counts, setCounts] = useState<Readonly<Record<string, { key: string; value: ReviewCount }>>>({});
    const [epoch, setEpoch] = useState(0);
    const known = useRef(counts);
    known.current = counts;

    useEffect(() => {
        let timer: number | undefined;
        const unsubscribe = subscribeImagesChanged(event => {
            if (event.data.projectId !== projectId || !event.data.images.length) return;
            window.clearTimeout(timer);
            timer = window.setTimeout(() => setEpoch(value => value + 1), 1000);
        });
        return () => {
            window.clearTimeout(timer);
            unsubscribe();
        };
    }, [projectId]);

    useEffect(() => {
        if (!enabled) return;
        const controller = new AbortController();
        const keyOf = (group: ImageGroupResponseDto) => `${group.revision}:${group.imageCount}:${group.archivedImageCount}:${epoch}`;
        const stale = groups.filter(group => !group.archivedAt && known.current[group.id]?.key !== keyOf(group));
        if (!stale.length) return;
        void (async () => {
            for (const group of stale) {
                const active = group.imageCount - group.archivedImageCount;
                let value: ReviewCount = null;
                if (active <= reviewCountLimit) {
                    try {
                        let reviewed = 0;
                        let after: string | undefined;
                        do {
                            const page = await imagesApi.list({ projectId, groupId: group.id, archive: "active", reviewState: "reviewed", limit: pageLimit, ...(after ? { after } : {}) },
                                controller.signal);
                            reviewed += page.items.length;
                            after = page.nextCursor ?? undefined;
                        } while (after && !controller.signal.aborted);
                        value = { reviewed, active };
                    } catch {
                        if (controller.signal.aborted) return;
                    }
                }
                if (controller.signal.aborted) return;
                setCounts(current => ({ ...current, [group.id]: { key: keyOf(group), value } }));
            }
        })();
        return () => controller.abort();
    }, [projectId, groups, enabled, epoch]);

    return (groupId: string): ReviewCount | undefined => counts[groupId]?.value;
}
