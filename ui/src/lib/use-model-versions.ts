import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeTrainingChanged } from "@/api/events/subscriptions";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { trainingApi } from "@/api/projects/training/training-api";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import "@/i18n/sections/training";

/**
 * Model versions of the open project: listModelVersions on opening and after training.changed with changed versions (a new one
 * appeared, "Working" changed, one was deleted — revision = null). renew — fresh image links (links of the resource channel live a limited time).
 */
export function useModelVersions(projectId: string) {
    const [versions, setVersions] = useState<readonly ModelVersionResponseDto[]>([]);
    const [error, setError] = useState<string>();
    /** The list was read at least once: a version missing from it was deleted. */
    const [loaded, setLoaded] = useState(false);
    /** The start time of the last list read: a version created later may be missing from it. */
    const [listedAt, setListedAt] = useState(0);
    const mounted = useRef(true);
    const read = useRef(0);

    const refresh = useCallback(async () => {
        const own = ++read.current;
        const startedAt = Date.now();
        try {
            const list = await trainingApi.listModelVersions({ projectId });
            if (!mounted.current || own !== read.current) return;
            setVersions(list);
            setLoaded(true);
            setListedAt(startedAt);
            setError(undefined);
        } catch (cause) {
            if (mounted.current && own === read.current) setError(errorMessage(cause, i18n.t("training:loadErrors.versions")));
        }
    }, [projectId]);

    const apply = useCallback((version: ModelVersionResponseDto) => {
        setVersions(current => current.some(item => item.id === version.id)
            ? current.map(item => item.id === version.id ? version : item.isWorking && version.isWorking ? { ...item, isWorking: false } : item)
            : [...current, version]);
    }, []);

    /** Image links of the version expired: read it again. */
    const renew = useCallback(async (versionId: string) => {
        try {
            const version = await trainingApi.getModelVersion({ projectId, versionId });
            if (mounted.current) apply(version);
        } catch {
            // The version is unavailable — the next event updates the list.
        }
    }, [projectId, apply]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeTrainingChanged(event => {
            if (event.data.projectId === projectId && event.data.versions.length) void refresh();
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh]);

    /** "Make working". */
    const setWorking = useCallback(async (versionId: string) => {
        apply(await trainingApi.setWorkingModelVersion({ projectId, versionId }));
    }, [projectId, apply]);

    return { versions, loaded, listedAt, error, refresh, renew, setWorking };
}
