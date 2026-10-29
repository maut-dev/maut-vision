import { useCallback, useEffect, useRef, useState } from "react";
import { classesApi } from "@/api/projects/catalog/classes-api";
import { importClassAssetsJobKind } from "@/api/projects/catalog/response-dto/import-class-assets-result-dto";
import { subscribeJobChanged } from "@/api/events/subscriptions";
import { jobsApi } from "@/api/jobs/jobs-api";
import type { JobResponseDto } from "@/api/jobs/response-dto/job-response-dto";
import { errorMessage } from "./format";

/** The import state of one binding: choosing files in the native dialog, the job or a start error. */
export interface AssetImport {
    selecting: boolean;
    job?: JobResponseDto;
    error?: string;
}

export const isJobActive = (job: JobResponseDto) => job.status === "queued" || job.status === "running";

const newer = (left: JobResponseDto | undefined, right: JobResponseDto | undefined) =>
    !left ? right : !right ? left : right.sequence > left.sequence ? right : left;

/**
 * Image import: one user action is a choice of files and one classes.import_assets job.
 * The progress and the result come with job.changed events; events that arrive before the answer of importAssets are not lost.
 */
export function useAssetImports(projectId: string) {
    const [imports, setImports] = useState<Readonly<Record<string, AssetImport>>>({});
    const bindingByJob = useRef(new Map<string, string>());
    const early = useRef(new Map<string, JobResponseDto>());
    const mounted = useRef(true);

    const update = useCallback((classVariantId: string, change: (current: AssetImport) => AssetImport) => {
        if (!mounted.current) return;
        setImports(current => ({ ...current, [classVariantId]: change(current[classVariantId] ?? { selecting: false }) }));
    }, []);

    const applyJob = useCallback((classVariantId: string, job: JobResponseDto) => {
        update(classVariantId, current => current.job && current.job.id === job.id && current.job.sequence >= job.sequence
            ? current
            : { selecting: false, job });
    }, [update]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeJobChanged(event => {
            const job = event.data;
            if (job.projectId !== projectId || job.kind !== importClassAssetsJobKind) return;
            const classVariantId = bindingByJob.current.get(job.id);
            if (classVariantId) applyJob(classVariantId, job);
            else {
                early.current.set(job.id, newer(early.current.get(job.id), job)!);
                // Only jobs started from this screen matter; foreign ones are dropped after a while.
                if (early.current.size > 32) early.current.delete(early.current.keys().next().value!);
            }
        });
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, applyJob]);

    async function start(classVariantId: string) {
        update(classVariantId, current => ({ ...current, selecting: true, error: undefined }));
        try {
            const selection = await classesApi.selectAssetFiles();
            if (!selection.selectionToken) {
                update(classVariantId, current => ({ ...current, selecting: false }));
                return;
            }
            const job = await classesApi.importAssets({
                projectId, classVariantId, selectionToken: selection.selectionToken, operationId: crypto.randomUUID(),
            });
            bindingByJob.current.set(job.id, classVariantId);
            const latest = newer(job, early.current.get(job.id))!;
            early.current.delete(job.id);
            applyJob(classVariantId, latest);
        } catch (cause) {
            update(classVariantId, current => ({ ...current, selecting: false, error: errorMessage(cause) }));
        }
    }

    async function cancel(classVariantId: string) {
        const job = imports[classVariantId]?.job;
        if (!job || !isJobActive(job)) return;
        try {
            applyJob(classVariantId, await jobsApi.cancel({ projectId, jobId: job.id }));
        } catch (cause) {
            update(classVariantId, current => ({ ...current, error: errorMessage(cause) }));
        }
    }

    function dismiss(classVariantId: string) {
        setImports(current => {
            const next = { ...current };
            delete next[classVariantId];
            return next;
        });
    }

    return { imports, start, cancel, dismiss };
}

export type AssetImportsState = ReturnType<typeof useAssetImports>;
