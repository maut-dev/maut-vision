import { useEffect, useMemo, useState } from "react";
import { imagesApi } from "@/api/projects/images/images-api";
import { importImagesJobKind } from "@/api/projects/images/response-dto/import-images-result-dto";
import type { VideoFrameRange } from "@/api/projects/images/video-frame-range";
import { i18n } from "@/i18n";
import "@/i18n/sections/images";
import { errorMessage } from "./format";
import { useJobHistory } from "./use-job-history";

export interface ImportOptions {
    /** "Skip duplicates in the group", on by default. */
    skipDuplicates: boolean;
    /** "Include subfolders": for a folder only. */
    recursive: boolean;
}

const listImports = (projectId: string) => (groupId: string, limit: number) => imagesApi.listImports({ projectId, groupId, limit });

/** Import into a group and the import history of this group only (useJobHistory: images.listImports, then job.changed). */
export function useImageImports(projectId: string, groupId: string | undefined) {
    const list = useMemo(() => listImports(projectId), [projectId]);
    const history = useJobHistory(projectId, groupId, importImagesJobKind, list, i18n.t("images:errors.loadImports"));
    const [selecting, setSelecting] = useState(false);
    const { apply, scope, isCurrent, setError } = history;

    useEffect(() => setSelecting(false), [projectId, groupId]);

    /** One user action: the native choice of files or a folder and one images.import job. */
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
     * Frames of the chosen video: one images.import job. A request error (for example, video.range_invalid) is thrown to the caller;
     * the selection token stays valid after it.
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
