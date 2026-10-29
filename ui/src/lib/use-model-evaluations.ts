import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeChecksChanged, subscribeJobChanged } from "@/api/events/subscriptions";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { StartModelEvaluationRequestDto } from "@/api/projects/checks/request-dto/checks-request-dto";
import { modelEvaluationJobKind, type ModelEvaluationResponseDto } from "@/api/projects/checks/response-dto/model-evaluation-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { i18n } from "@/i18n";
import "@/i18n/sections/checks";
import { errorMessage } from "./format";

/** Rows of all checks. */
export const evaluationListLimit = 200;

const newestFirst = (left: ModelEvaluationResponseDto, right: ModelEvaluationResponseDto) =>
    right.job.createdAt.localeCompare(left.job.createdAt) || right.id.localeCompare(left.id);

/** The later of two evaluation snapshots: by revision, on equal ones by the job sequence. */
function newer(known: ModelEvaluationResponseDto | undefined, next: ModelEvaluationResponseDto): ModelEvaluationResponseDto {
    if (!known) return next;
    if (next.revision !== known.revision) return next.revision > known.revision ? next : known;
    return next.job.sequence >= known.job.sequence ? next : known;
}

/**
 * Dataset evaluations of the versions of the open project (all checks and the comparison matrix) without polling: listEvaluations and
 * listLatestEvaluations on opening; checks.changed with a revision newer than the known one or job.changed of a checks.evaluate job —
 * getEvaluation; revision = null — the evaluation was deleted together with the version. The matrix is read again when an evaluation succeeds.
 * Shared by Checks and Analytics.
 */
export function useModelEvaluations(projectId: string) {
    const [evaluations, setEvaluations] = useState<readonly ModelEvaluationResponseDto[]>([]);
    const [latest, setLatest] = useState<readonly ModelEvaluationResponseDto[]>([]);
    const [total, setTotal] = useState(0);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const known = useRef(evaluations);
    known.current = evaluations;
    const reads = useRef(new Map<string, number>());
    const sequence = useRef(0);
    const latestRead = useRef(0);

    const apply = useCallback((evaluation: ModelEvaluationResponseDto) => {
        setEvaluations(current => {
            const existing = current.find(item => item.id === evaluation.id);
            if (!existing) return [...current, evaluation].sort(newestFirst);
            const next = newer(existing, evaluation);
            return next === existing ? current : current.map(item => item.id === evaluation.id ? next : item);
        });
    }, []);

    const remove = useCallback((evaluationId: string) => {
        setEvaluations(current => current.filter(item => item.id !== evaluationId));
        setLatest(current => current.filter(item => item.id !== evaluationId));
    }, []);

    const refreshLatest = useCallback(async () => {
        const own = ++latestRead.current;
        try {
            const list = await checksApi.listLatestEvaluations({ projectId });
            if (mounted.current && own === latestRead.current) setLatest(list);
        } catch {
            // The matrix refreshes with the next evaluation.
        }
    }, [projectId]);

    const reread = useCallback(async (evaluationId: string) => {
        const read = ++sequence.current;
        reads.current.set(evaluationId, read);
        try {
            const evaluation = await checksApi.getEvaluation({ projectId, evaluationId });
            if (!mounted.current || reads.current.get(evaluationId) !== read) return;
            const before = known.current.find(item => item.id === evaluationId);
            if (!before) setTotal(value => value + 1);
            apply(evaluation);
            if (evaluation.state === "succeeded" && before?.state !== "succeeded") void refreshLatest();
        } catch (cause) {
            if (mounted.current && cause instanceof BridgeError && cause.code === "checks.evaluation_not_found") remove(evaluationId);
        }
    }, [projectId, apply, remove, refreshLatest]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const [page] = await Promise.all([checksApi.listEvaluations({ projectId, limit: evaluationListLimit }), refreshLatest()]);
            if (!mounted.current) return;
            setEvaluations(current => page.items.map(item => newer(current.find(entry => entry.id === item.id), item)));
            setTotal(page.total);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, i18n.t("checks:evaluation.loadFailed")));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId, refreshLatest]);

    useEffect(() => {
        mounted.current = true;
        const offChecks = subscribeChecksChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const change of event.data.evaluations) {
                if (change.revision === null) remove(change.id);
                else if ((known.current.find(item => item.id === change.id)?.revision ?? -1) < change.revision) void reread(change.id);
            }
        });
        const offJobs = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== modelEvaluationJobKind) return;
            const existing = known.current.find(item => item.id === job.id);
            if (!existing || existing.job.sequence < job.sequence) void reread(job.id);
        });
        void refresh();
        return () => {
            mounted.current = false;
            offChecks();
            offJobs();
        };
    }, [projectId, refresh, reread, remove]);

    /** Check on the dataset: the new evaluation shows in the list at once; progress and outcome come with events. */
    const start = useCallback(async (request: Omit<StartModelEvaluationRequestDto, "projectId" | "operationId">) => {
        const evaluation = await checksApi.startEvaluation({ ...request, projectId, operationId: crypto.randomUUID() });
        if (!known.current.some(item => item.id === evaluation.id)) setTotal(value => value + 1);
        apply(evaluation);
        return evaluation;
    }, [projectId, apply]);

    /** Cancel. */
    const cancel = useCallback(async (evaluationId: string) => {
        apply(await checksApi.cancelEvaluation({ projectId, evaluationId }));
    }, [projectId, apply]);

    return { evaluations, latest, total, loading, error, refresh, start, cancel };
}

export type ModelEvaluationsState = ReturnType<typeof useModelEvaluations>;
