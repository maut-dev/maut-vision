import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeJobChanged, subscribeTrainingChanged } from "@/api/events/subscriptions";
import { trainingJobKind, type TrainingRunResponseDto } from "@/api/projects/training/response-dto/training-run-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { BridgeError } from "@/api/transport/errors";
import { errorMessage } from "./format";
import { newerRun, withJob } from "./training/run-view";

/** Запусков на странице «История обучения». */
export const trainingRunPageLimit = 50;

const newestFirst = (left: TrainingRunResponseDto, right: TrainingRunResponseDto) =>
    right.job.createdAt.localeCompare(left.job.createdAt) || right.id.localeCompare(left.id);

/**
 * Запуски обучения открытого проекта без опроса: listRuns при открытии (и «Ещё запуски» страницами); training.changed с ревизией
 * новее известной — getRun; job.changed (kind training.run) — состояние и прогресс сразу из снимка задачи; запуск, которого нет
 * в списке (поставлен в другом окне), читается getRun. Ответы startRun и cancelRun применяются сразу.
 */
export function useTrainingRuns(projectId: string) {
    const [runs, setRuns] = useState<readonly TrainingRunResponseDto[]>([]);
    const [total, setTotal] = useState(0);
    const [loading, setLoading] = useState(true);
    const [loadingMore, setLoadingMore] = useState(false);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const latest = useRef(runs);
    latest.current = runs;
    const reads = useRef(new Map<string, number>());
    const sequence = useRef(0);

    const apply = useCallback((run: TrainingRunResponseDto) => {
        setRuns(current => {
            const known = current.find(item => item.id === run.id);
            if (!known) return [...current, run].sort(newestFirst);
            const next = newerRun(known, run);
            return next === known ? current : current.map(item => item.id === run.id ? next : item);
        });
    }, []);

    const reread = useCallback(async (runId: string) => {
        const read = ++sequence.current;
        reads.current.set(runId, read);
        try {
            const run = await trainingApi.getRun({ projectId, runId });
            if (mounted.current && reads.current.get(runId) === read) {
                if (!latest.current.some(item => item.id === runId)) setTotal(value => value + 1);
                apply(run);
            }
        } catch (cause) {
            if (mounted.current && cause instanceof BridgeError && cause.code === "training.run_not_found") {
                setRuns(current => current.filter(item => item.id !== runId));
            }
        }
    }, [projectId, apply]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const page = await trainingApi.listRuns({ projectId, limit: trainingRunPageLimit });
            if (!mounted.current) return;
            setRuns(current => page.items.map(item => newerRun(current.find(known => known.id === item.id), item)));
            setTotal(page.total);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, "Не удалось загрузить запуски обучения. Повторите загрузку."));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    /** «Ещё запуски»: следующая страница после последнего загруженного. */
    const loadMore = useCallback(async () => {
        const last = latest.current[latest.current.length - 1];
        if (!last) return;
        setLoadingMore(true);
        try {
            const page = await trainingApi.listRuns({ projectId, limit: trainingRunPageLimit, before: last.id });
            if (!mounted.current) return;
            for (const item of page.items) apply(item);
            setTotal(page.total);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause));
        } finally {
            if (mounted.current) setLoadingMore(false);
        }
    }, [projectId, apply]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribeTraining = subscribeTrainingChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const change of event.data.runs) {
                if (change.revision === null) {
                    setRuns(current => current.filter(item => item.id !== change.id));
                    continue;
                }
                const known = latest.current.find(item => item.id === change.id);
                if (!known || known.revision < change.revision) void reread(change.id);
            }
        });
        const unsubscribeJobs = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== trainingJobKind) return;
            if (latest.current.some(item => item.id === job.id)) setRuns(current => current.map(item => item.id === job.id ? withJob(item, job) : item));
            else void reread(job.id);
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribeTraining();
            unsubscribeJobs();
        };
    }, [projectId, refresh, reread]);

    /** «Отменить обучение»: ответ применяется сразу, итог — job.changed. */
    const cancel = useCallback(async (runId: string) => {
        apply(await trainingApi.cancelRun({ projectId, runId }));
    }, [projectId, apply]);

    return { runs, total: Math.max(total, runs.length), loading, loadingMore, error, refresh, loadMore, apply, cancel };
}
