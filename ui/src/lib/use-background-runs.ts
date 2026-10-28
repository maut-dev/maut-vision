import { useMemo } from "react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import { importBackgroundsJobKind } from "@/api/projects/backgrounds/response-dto/import-backgrounds-result-dto";
import { useJobHistory } from "./use-job-history";

/**
 * История подготовки только этого набора (backgrounds.listRuns, затем job.changed): прогресс обработано/всего, итог с
 * причинами пропуска и отмена. apply — снимок задачи, которую только что поставил UI.
 */
export function useBackgroundRuns(projectId: string, setId: string | undefined) {
    const list = useMemo(() => (target: string, limit: number) => backgroundsApi.listRuns({ projectId, setId: target, limit }), [projectId]);
    return useJobHistory(projectId, setId, importBackgroundsJobKind, list, "Не удалось загрузить историю подготовки.");
}

export type BackgroundRunsState = ReturnType<typeof useBackgroundRuns>;
