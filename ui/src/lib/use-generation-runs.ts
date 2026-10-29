import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeGenerationChanged, subscribeJobChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { generationApi } from "@/api/projects/generation/generation-api";
import { generationRunJobKind, type GenerationRunResponseDto } from "@/api/projects/generation/response-dto/generation-run-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import "@/i18n/sections/generation";
import { isRunActive } from "./generation/run-state";

/** How many last runs the tab "Runs" shows. */
export const runPageLimit = 50;

const newestFirst = (left: GenerationRunResponseDto, right: GenerationRunResponseDto) =>
    right.job.createdAt.localeCompare(left.job.createdAt) || right.job.id.localeCompare(left.job.id);

/**
 * Generation runs of the open project without polling: generation.listRuns on opening; state, progress and result — job.changed
 * (kind generation.run), a job snapshot applies only when its sequence is newer; a run missing from the list
 * (started elsewhere) is read with getRun. generation.changed.runs (prepared — a group appeared, deleted) reads the run again:
 * generation.unavailable removes it from the list.
 */
export function useGenerationRuns(projectId: string) {
    const [runs, setRuns] = useState<readonly GenerationRunResponseDto[]>([]);
    const [pageTotal, setPageTotal] = useState(0);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const latest = useRef(runs);
    latest.current = runs;
    /** Runs deleted in this tab: a late job snapshot does not bring them back to the list. */
    const removed = useRef(new Set<string>());
    /** Runs of the last list read: "Runs (N)" = total of the read + new ones − those deleted from it. */
    const listed = useRef(new Set<string>());

    const apply = useCallback((run: GenerationRunResponseDto) => {
        if (removed.current.has(run.job.id)) return;
        setRuns(current => {
            const known = current.find(item => item.job.id === run.job.id);
            if (!known) return [run, ...current].sort(newestFirst);
            // The newer job snapshot wins; the run data (group, counts) comes from the latest read.
            const job = known.job.sequence > run.job.sequence ? known.job : run.job;
            return current.map(item => item.job.id === run.job.id ? { ...run, job } : item);
        });
    }, []);

    const applyJob = useCallback((job: JobResponseDto) => {
        setRuns(current => current.map(item => item.job.id === job.id && job.sequence > item.job.sequence ? { ...item, job } : item));
    }, []);

    const remove = useCallback((runId: string) => {
        removed.current.add(runId);
        setRuns(current => current.filter(item => item.job.id !== runId));
    }, []);

    const reread = useCallback(async (runId: string) => {
        try {
            const run = await generationApi.getRun({ projectId, runId });
            if (mounted.current) apply(run);
        } catch (cause) {
            if (mounted.current && cause instanceof BridgeError && cause.code === "generation.unavailable") remove(runId);
        }
    }, [projectId, apply, remove]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const page = await generationApi.listRuns({ projectId, limit: runPageLimit });
            if (!mounted.current) return;
            setRuns(current => page.items.filter(run => !removed.current.has(run.job.id)).map(run => {
                const known = current.find(item => item.job.id === run.job.id);
                return known && known.job.sequence > run.job.sequence ? { ...run, job: known.job } : run;
            }));
            listed.current = new Set(page.items.map(run => run.job.id));
            setPageTotal(page.total);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, i18n.t("generation:loadErrors.runs")));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribeJobs = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== generationRunJobKind || removed.current.has(job.id)) return;
            if (latest.current.some(item => item.job.id === job.id)) applyJob(job);
            else void reread(job.id);
        });
        const unsubscribeGeneration = subscribeGenerationChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const runId of event.data.runs) void reread(runId);
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribeJobs();
            unsubscribeGeneration();
        };
    }, [projectId, refresh, reread, applyJob]);

    /** "Cancel": the job snapshot from the answer applies at once, then events. */
    const cancel = useCallback(async (run: GenerationRunResponseDto) => {
        if (!isRunActive(run.job)) return;
        applyJob(await jobsApi.cancel({ projectId, jobId: run.job.id }));
    }, [projectId, applyJob]);

    const added = runs.filter(run => !listed.current.has(run.job.id)).length;
    const gone = [...removed.current].filter(id => listed.current.has(id)).length;
    return { runs, total: Math.max(runs.length, pageTotal + added - gone), loading, error, refresh, apply, remove, cancel };
}

export type GenerationRunsState = ReturnType<typeof useGenerationRuns>;
