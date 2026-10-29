import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeBackgroundsChanged, subscribeProjectChanged } from "@/api/events/subscriptions";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { BridgeError } from "@/api/transport/errors";
import "@/components/backgrounds/locale";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";

const byCreation = (left: BackgroundSetResponseDto, right: BackgroundSetResponseDto) =>
    left.createdAt.localeCompare(right.createdAt) || left.id.localeCompare(right.id);

/**
 * Background sets of the open project: backgrounds.listSets on opening, then backgrounds.changed rereads only the sets named by the
 * event (a new revision, deletion, a change of contents and counters). project.changed rereads the list: matchesProjectInput depends
 * on the project input. An answer started before the next read of the same set is dropped.
 */
export function useBackgroundSets(projectId: string) {
    const [sets, setSets] = useState<readonly BackgroundSetResponseDto[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const reads = useRef(new Map<string, number>());
    const sequence = useRef(0);
    const listing = useRef(0);

    const remove = useCallback((setId: string) => {
        setSets(current => current.filter(item => item.id !== setId));
    }, []);

    /** The answer of an operation or a read; an older revision does not replace a newer one (counters change without a set revision). */
    const apply = useCallback((set: BackgroundSetResponseDto) => {
        setSets(current => {
            const known = current.find(item => item.id === set.id);
            if (known && known.revision > set.revision) return current;
            return known ? current.map(item => item.id === set.id ? set : item) : [...current, set].sort(byCreation);
        });
    }, []);

    const reread = useCallback(async (setId: string) => {
        const read = ++sequence.current;
        reads.current.set(setId, read);
        try {
            const set = await backgroundsApi.getSet({ projectId, setId });
            if (mounted.current && reads.current.get(setId) === read) apply(set);
        } catch (cause) {
            if (!mounted.current || reads.current.get(setId) !== read) return;
            if (cause instanceof BridgeError && cause.code === "backgrounds.set_not_found") remove(setId);
        }
    }, [projectId, apply, remove]);

    const refresh = useCallback(async () => {
        const read = ++listing.current;
        setLoading(true);
        try {
            const list = await backgroundsApi.listSets({ projectId });
            if (!mounted.current || read !== listing.current) return;
            setSets([...list].sort(byCreation));
            setError(undefined);
        } catch (cause) {
            if (mounted.current && read === listing.current) setError(errorMessage(cause, i18n.t("backgrounds:errors.loadSets")));
        } finally {
            if (mounted.current && read === listing.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeBackgroundsChanged(event => {
            if (event.data.projectId !== projectId) return;
            const ids = new Set(event.data.itemsChangedIn);
            for (const change of event.data.sets) {
                if (change.revision === null) {
                    ids.delete(change.id);
                    reads.current.delete(change.id);
                    remove(change.id);
                } else ids.add(change.id);
            }
            for (const id of ids) void reread(id);
        });
        const unsubscribeProject = subscribeProjectChanged(event => {
            if (event.data.id === projectId) void refresh();
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
            unsubscribeProject();
        };
    }, [projectId, refresh, reread, remove]);

    return { sets, loading, error, refresh, apply, remove, reread };
}

export type BackgroundSetsState = ReturnType<typeof useBackgroundSets>;
