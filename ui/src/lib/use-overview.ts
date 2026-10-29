import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeDatasetsChanged, subscribeJobChanged, subscribeTrainingChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { ActiveJobResponseDto } from "@/api/jobs/response-dto/active-jobs-page-response-dto";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { overviewApi } from "@/api/projects/overview/overview-api";
import type { OverviewResponseDto } from "@/api/projects/overview/response-dto/overview-response-dto";

/** The pause over which events merge into one reading of the overview. */
const refreshDelayMs = 200;

const isActive = (job: JobResponseDto) => job.status === "queued" || job.status === "running";

/** The overview snapshot without active jobs, and the loaded pages of active jobs (Show more jobs). */
export interface OverviewState {
    overview: Omit<OverviewResponseDto, "activeJobs">;
    jobs: readonly ActiveJobResponseDto[];
    total: number;
    nextCursor: string | null;
}

/**
 * The overview of the open project without polling: overview.get on opening; datasets.changed and training.changed with versions — read the
 * overview again; job.changed of a running job from the list is applied in place (by sequence), a new or finished job — read again. Events
 * within 200 ms merge, one reading runs at a time. Reading again keeps the number of loaded job pages.
 */
export function useOverview(projectId: string) {
    const [state, setState] = useState<OverviewState>();
    const [error, setError] = useState(false);
    const [loadingMore, setLoadingMore] = useState(false);
    const latest = useRef(state);
    latest.current = state;
    const pages = useRef(1);
    const mounted = useRef(true);
    const reading = useRef(false);
    const dirty = useRef(false);
    const timer = useRef<number>(undefined);

    const read = useCallback(async () => {
        if (reading.current) {
            dirty.current = true;
            return;
        }
        reading.current = true;
        dirty.current = false;
        try {
            const { activeJobs, ...overview } = await overviewApi.get({ projectId });
            const jobs = [...activeJobs.items];
            let cursor = activeJobs.nextCursor;
            let total = activeJobs.total;
            for (let page = 1; page < pages.current && cursor && mounted.current; page++) {
                const next = await jobsApi.listActive({ projectId, after: cursor });
                jobs.push(...next.items.filter(item => !jobs.some(known => known.job.id === item.job.id)));
                cursor = next.nextCursor;
                total = next.total;
            }
            if (!mounted.current) return;
            // A job snapshot from an event that came during the reading is newer than the answer.
            setState(current => ({
                overview,
                jobs: jobs.map(item => {
                    const known = current?.jobs.find(entry => entry.job.id === item.job.id);
                    return known && known.job.sequence > item.job.sequence && known.job.status === item.job.status ? { ...item, job: known.job } : item;
                }),
                total,
                nextCursor: cursor,
            }));
            setError(false);
        } catch {
            if (mounted.current) setError(true);
        } finally {
            reading.current = false;
            if (mounted.current && dirty.current) void read();
        }
    }, [projectId]);

    const schedule = useCallback(() => {
        window.clearTimeout(timer.current);
        timer.current = window.setTimeout(() => void read(), refreshDelayMs);
    }, [read]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribeJobs = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId) return;
            const known = latest.current?.jobs.find(item => item.job.id === job.id);
            if (known && isActive(job) && known.job.status === job.status) {
                if (known.job.sequence >= job.sequence) return;
                setState(current => current && {
                    ...current,
                    jobs: current.jobs.map(item => item.job.id === job.id && item.job.sequence < job.sequence ? { ...item, job } : item),
                });
                return;
            }
            if (known && known.job.sequence >= job.sequence) return;
            schedule();
        });
        const unsubscribeDatasets = subscribeDatasetsChanged(event => {
            if (event.data.projectId === projectId) schedule();
        });
        const unsubscribeTraining = subscribeTrainingChanged(event => {
            if (event.data.projectId === projectId && event.data.versions.length) schedule();
        });
        void read();
        return () => {
            mounted.current = false;
            window.clearTimeout(timer.current);
            unsubscribeJobs();
            unsubscribeDatasets();
            unsubscribeTraining();
        };
    }, [projectId, read, schedule]);

    /** Show more jobs: the next page after the last loaded one. */
    const loadMore = useCallback(async () => {
        const cursor = latest.current?.nextCursor;
        if (!cursor) return;
        setLoadingMore(true);
        try {
            const page = await jobsApi.listActive({ projectId, after: cursor });
            if (!mounted.current) return;
            pages.current++;
            setState(current => current && {
                ...current,
                jobs: [...current.jobs, ...page.items.filter(item => !current.jobs.some(known => known.job.id === item.job.id))],
                total: page.total,
                nextCursor: page.nextCursor,
            });
        } catch {
            if (mounted.current) setError(true);
        } finally {
            if (mounted.current) setLoadingMore(false);
        }
    }, [projectId]);

    return { state, error, loadingMore, loadMore, retry: read };
}
