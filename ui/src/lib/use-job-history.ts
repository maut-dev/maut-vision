import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeJobChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { errorMessage } from "./format";
import { isJobActive } from "./use-asset-imports";

export const jobHistoryLimit = 20;

/**
 * The job history of one kind for one target (an image group, a background set): list is read when the target is chosen, then
 * job.changed. A job snapshot applies only when its sequence is newer. A job of this kind missing from the history
 * (started elsewhere) reads the history again once: a job snapshot does not name its target. A target change starts a new
 * generation — answers and starts of the previous one are dropped (scope/isCurrent).
 */
export function useJobHistory(projectId: string, targetId: string | undefined, kind: string,
    list: (targetId: string, limit: number) => Promise<JobResponseDto[]>, failText: string) {
    const [jobs, setJobs] = useState<readonly JobResponseDto[]>([]);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const generation = useRef(0);
    const reading = useRef<Promise<void> | null>(null);
    const again = useRef(false);
    const latest = useRef(jobs);
    latest.current = jobs;
    const listRef = useRef(list);
    listRef.current = list;
    /** Jobs of the last history read and jobs of other targets: their events do not read the history again. */
    const listed = useRef(new Set<string>());
    const foreign = useRef(new Set<string>());

    const apply = useCallback((job: JobResponseDto) => {
        setJobs(current => {
            const known = current.find(item => item.id === job.id);
            if (known && known.sequence >= job.sequence) return current;
            return known ? current.map(item => item.id === job.id ? job : item) : [job, ...current].slice(0, jobHistoryLimit);
        });
    }, []);

    const refresh = useCallback((): Promise<void> => {
        if (!targetId) return Promise.resolve();
        if (reading.current) {
            again.current = true;
            return reading.current;
        }
        const current = generation.current;
        const work = (async () => {
            try {
                do {
                    again.current = false;
                    const read = await listRef.current(targetId, jobHistoryLimit);
                    if (!mounted.current || current !== generation.current) return;
                    listed.current = new Set(read.map(job => job.id));
                    setJobs(known => read.map(job => {
                        const local = known.find(item => item.id === job.id);
                        return local && local.sequence > job.sequence ? local : job;
                    }));
                } while (again.current);
            } catch (cause) {
                if (mounted.current && current === generation.current) setError(errorMessage(cause, failText));
            } finally {
                // A read of the previous target must not release the read of the current one.
                if (current === generation.current) reading.current = null;
            }
        })();
        reading.current = work;
        return work;
    }, [targetId, failText]);

    useEffect(() => {
        mounted.current = true;
        generation.current++;
        reading.current = null;
        listed.current = new Set();
        foreign.current = new Set();
        setJobs([]);
        setError(undefined);
        void refresh();
        const unsubscribe = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== kind) return;
            if (latest.current.some(item => item.id === job.id)) apply(job);
            else if (!foreign.current.has(job.id)) {
                void refresh().then(() => {
                    if (listed.current.has(job.id)) apply(job);
                    else foreign.current.add(job.id);
                });
            }
        });
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, kind, refresh, apply]);

    /** The generation of the target when an action started; isCurrent — the action still belongs to the chosen target. */
    const scope = useCallback(() => generation.current, []);
    const isCurrent = useCallback((value: number) => mounted.current && value === generation.current, []);

    const cancel = useCallback(async (job: JobResponseDto) => {
        if (!isJobActive(job)) return;
        try {
            apply(await jobsApi.cancel({ projectId, jobId: job.id }));
        } catch (cause) {
            setError(errorMessage(cause));
        }
    }, [projectId, apply]);

    return { jobs, error, setError, apply, refresh, scope, isCurrent, cancel, active: jobs.some(isJobActive) };
}
