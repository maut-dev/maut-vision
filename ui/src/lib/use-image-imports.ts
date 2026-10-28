import { useEffect, useMemo, useState } from "react";
import { imagesApi } from "@/api/projects/images/images-api";
import { importImagesJobKind } from "@/api/projects/images/response-dto/import-images-result-dto";
import type { VideoFrameRange } from "@/api/projects/images/video-frame-range";
import { errorMessage } from "./format";
import { useJobHistory } from "./use-job-history";

export interface ImportOptions {
    /** «Пропускать повторы в группе», по умолчанию да. */
    skipDuplicates: boolean;
    /** «Включая вложенные папки» — только для папки. */
    recursive: boolean;
}

const listImports = (projectId: string) => (groupId: string, limit: number) => imagesApi.listImports({ projectId, groupId, limit });

/** Импорт в группу и история импорта только этой группы (useJobHistory: images.listImports, затем job.changed). */
export function useImageImports(projectId: string, groupId: string | undefined) {
    const list = useMemo(() => listImports(projectId), [projectId]);
    const history = useJobHistory(projectId, groupId, importImagesJobKind, list, "Не удалось загрузить историю импорта.");
    const [selecting, setSelecting] = useState(false);
    const { apply, scope, isCurrent, setError } = history;

    useEffect(() => setSelecting(false), [projectId, groupId]);

    /** Одно действие пользователя: нативный выбор файлов или папки и одна задача images.import. */
    async function start(kind: "files" | "folder", options: ImportOptions) {
        if (!groupId) return;
        const current = scope();
        setSelecting(true);
        setError(undefined);
        try {
            const selection = kind === "files" ? await imagesApi.selectFiles() : await imagesApi.selectFolder();
            if (!selection.selectionToken || !isCurrent(current)) return;
            const job = await imagesApi.import({
                projectId,
                groupId,
                selectionToken: selection.selectionToken,
                operationId: crypto.randomUUID(),
                skipDuplicates: options.skipDuplicates,
                recursive: kind === "folder" ? options.recursive : undefined,
            });
            if (isCurrent(current)) apply(job);
        } catch (cause) {
            if (isCurrent(current)) setError(errorMessage(cause));
        } finally {
            if (isCurrent(current)) setSelecting(false);
        }
    }

    /**
     * Кадры выбранного видео: одна задача images.import. Ошибка запроса (например, video.range_invalid) выбрасывается
     * вызывающему; токен выбора после неё остаётся действительным.
     */
    async function importVideo(selectionToken: string, range: VideoFrameRange, skipDuplicates: boolean) {
        if (!groupId) return;
        const current = scope();
        const job = await imagesApi.import({ projectId, groupId, selectionToken, operationId: crypto.randomUUID(), skipDuplicates, ...range });
        if (isCurrent(current)) apply(job);
    }

    return {
        jobs: history.jobs,
        selecting,
        error: history.error,
        active: history.active,
        start,
        importVideo,
        cancel: history.cancel,
        dismissError: () => setError(undefined),
    };
}

export type ImageImportsState = ReturnType<typeof useImageImports>;
