import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeDatasetsChanged, subscribeJobChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { datasetsApi } from "@/api/projects/datasets/datasets-api";
import type { DatasetOperationKind } from "@/api/projects/datasets/request-dto/dataset-request-dto";
import { datasetJobKinds, type DatasetOperationResponseDto } from "@/api/projects/datasets/response-dto/dataset-operation-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { isOperationActive } from "./datasets/dataset-view";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";

/** How many last operations of a kind the tab shows. */
export const operationPageLimit = 50;

const newestFirst = (left: DatasetOperationResponseDto, right: DatasetOperationResponseDto) =>
    right.job.createdAt.localeCompare(left.job.createdAt) || right.job.id.localeCompare(left.job.id);

/** Keys of the texts shown when the history of a kind fails to load. */
const failTexts = {
    build: "datasets:loadErrors.build",
    verify: "datasets:loadErrors.verify",
    export: "datasets:loadErrors.export",
} as const satisfies Record<DatasetOperationKind, string>;

/**
 * The history of operations of one kind (builds, checks, exports) without polling: datasets.listOperations on opening; state,
 * progress and result — job.changed (kind datasets.<kind>), a snapshot applies only when its sequence is newer; an operation
 * missing from the list (started elsewhere) is read with getOperation. Deleting a version (datasets.changed with null) deletes its
 * history too — the cards of this version go.
 */
export function useDatasetOperations(projectId: string, kind: DatasetOperationKind) {
    const [operations, setOperations] = useState<readonly DatasetOperationResponseDto[]>([]);
    const [pageTotal, setPageTotal] = useState(0);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const latest = useRef(operations);
    latest.current = operations;
    const removed = useRef(new Set<string>());
    const listed = useRef(new Set<string>());

    const apply = useCallback((operation: DatasetOperationResponseDto) => {
        if (removed.current.has(operation.job.id)) return;
        setOperations(current => {
            const known = current.find(item => item.job.id === operation.job.id);
            if (!known) return [operation, ...current].sort(newestFirst);
            const job = known.job.sequence > operation.job.sequence ? known.job : operation.job;
            return current.map(item => item.job.id === operation.job.id ? { ...operation, job } : item);
        });
    }, []);

    const applyJob = useCallback((job: JobResponseDto) => {
        setOperations(current => current.map(item => item.job.id === job.id && job.sequence > item.job.sequence ? { ...item, job } : item));
    }, []);

    const remove = useCallback((operationId: string) => {
        removed.current.add(operationId);
        setOperations(current => current.filter(item => item.job.id !== operationId));
    }, []);

    const reread = useCallback(async (operationId: string) => {
        try {
            const operation = await datasetsApi.getOperation({ projectId, operationId });
            if (mounted.current && operation.kind === kind) apply(operation);
        } catch (cause) {
            if (mounted.current && cause instanceof BridgeError && cause.code === "datasets.unavailable") remove(operationId);
        }
    }, [projectId, kind, apply, remove]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const page = await datasetsApi.listOperations({ projectId, kind, limit: operationPageLimit });
            if (!mounted.current) return;
            setOperations(current => page.items.filter(item => !removed.current.has(item.job.id)).map(item => {
                const known = current.find(entry => entry.job.id === item.job.id);
                return known && known.job.sequence > item.job.sequence ? { ...item, job: known.job } : item;
            }));
            listed.current = new Set(page.items.map(item => item.job.id));
            setPageTotal(page.total);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, i18n.t(failTexts[kind])));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId, kind]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribeJobs = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== datasetJobKinds[kind] || removed.current.has(job.id)) return;
            if (latest.current.some(item => item.job.id === job.id)) applyJob(job);
            else void reread(job.id);
        });
        const unsubscribeDatasets = subscribeDatasetsChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const change of event.data.versions) {
                if (change.revision !== null) continue;
                for (const item of latest.current) if (item.versionId === change.id && !isOperationActive(item.job)) remove(item.job.id);
            }
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribeJobs();
            unsubscribeDatasets();
        };
    }, [projectId, kind, refresh, reread, applyJob, remove]);

    /** "Cancel": the job snapshot from the answer applies at once, then events. */
    const cancel = useCallback(async (operation: DatasetOperationResponseDto) => {
        if (!isOperationActive(operation.job)) return;
        applyJob(await jobsApi.cancel({ projectId, jobId: operation.job.id }));
    }, [projectId, applyJob]);

    const added = operations.filter(item => !listed.current.has(item.job.id)).length;
    const gone = [...removed.current].filter(id => listed.current.has(id)).length;
    return { operations, total: Math.max(operations.length, pageTotal + added - gone), loading, error, refresh, apply, remove, cancel };
}

export type DatasetOperationsState = ReturnType<typeof useDatasetOperations>;
