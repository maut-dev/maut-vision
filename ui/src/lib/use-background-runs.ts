import { useMemo } from "react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import { importBackgroundsJobKind } from "@/api/projects/backgrounds/response-dto/import-backgrounds-result-dto";
import "@/i18n/sections/backgrounds";
import { i18n } from "@/i18n";
import { useJobHistory } from "./use-job-history";

/**
 * The preparation history of this set only (backgrounds.listRuns, then job.changed): processed/total progress, the result with skip
 * reasons, and cancelling. apply takes the snapshot of a job the UI has just started.
 */
export function useBackgroundRuns(projectId: string, setId: string | undefined) {
    const list = useMemo(() => (target: string, limit: number) => backgroundsApi.listRuns({ projectId, setId: target, limit }), [projectId]);
    return useJobHistory(projectId, setId, importBackgroundsJobKind, list, i18n.t("backgrounds:errors.loadRuns"));
}

export type BackgroundRunsState = ReturnType<typeof useBackgroundRuns>;
