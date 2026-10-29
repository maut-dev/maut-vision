import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeJobChanged } from "@/api/events/subscriptions";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { isModelExportResultDto, modelExportJobKind, type ModelExportResultDto } from "@/api/projects/training/response-dto/model-version-deletion-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { isJobActive } from "./use-asset-imports";

export interface ModelExportState {
    versionId: string;
    versionNumber: number;
    fileName: string;
    job: JobResponseDto;
    result: ModelExportResultDto | null;
}

/**
 * "Export ZIP": the native "Save as" with model-vN.zip (a closed dialog starts nothing), then the job training.export —
 * progress and result by job.changed of this job. Job snapshots that arrive before the start answer are not lost: the last by sequence.
 */
export function useModelExport(projectId: string) {
    const [state, setState] = useState<ModelExportState>();
    const seen = useRef(new Map<string, JobResponseDto>());
    const mounted = useRef(true);

    const applyJob = useCallback((job: JobResponseDto) => {
        setState(current => {
            if (!current || current.job.id !== job.id || current.job.sequence >= job.sequence) return current;
            return { ...current, job, result: isModelExportResultDto(job.result) ? job.result : null };
        });
    }, []);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== modelExportJobKind) return;
            const known = seen.current.get(job.id);
            if (!known || known.sequence < job.sequence) seen.current.set(job.id, job);
            applyJob(job);
        });
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, applyJob]);

    /** true — the export started, false — the "Save as" dialog was closed. */
    const exportVersion = useCallback(async (versionId: string, versionNumber: number): Promise<boolean> => {
        const destination = await trainingApi.selectModelExportDestination({ projectId, versionId });
        if (!destination.token) return false;
        const started = await trainingApi.startModelExport({ projectId, versionId, destinationToken: destination.token, operationId: crypto.randomUUID() });
        const early = seen.current.get(started.id);
        const job = early && early.sequence > started.sequence ? early : started;
        if (mounted.current) {
            setState({ versionId, versionNumber, fileName: destination.fileName ?? `model-v${versionNumber}.zip`, job,
                result: isModelExportResultDto(job.result) ? job.result : null });
        }
        return true;
    }, [projectId]);

    const active = !!state && isJobActive(state.job);
    return { state, active, exportVersion, dismiss: () => setState(undefined) };
}
