import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeBackgroundsChanged } from "@/api/events/subscriptions";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { BackgroundRemovedFilter } from "@/api/projects/backgrounds/request-dto/list-backgrounds-request-dto";
import type { BackgroundResponseDto } from "@/api/projects/backgrounds/response-dto/background-response-dto";
import { BridgeError } from "@/api/transport/errors";
import "@/i18n/sections/backgrounds";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";

export const backgroundPageSize = 100;
/** The page limit of the API: the tail is reread with one page. */
const tailLimit = 200;

interface GalleryState {
    items: readonly BackgroundResponseDto[];
    /** The cursor of the next page; null when everything is loaded. */
    nextCursor: string | null;
    loading: boolean;
    error?: string;
}

const emptyState: GalleryState = { items: [], nextCursor: null, loading: false };

export function matchesRemovedFilter(item: BackgroundResponseDto, setId: string, filter: BackgroundRemovedFilter): boolean {
    if (item.setId !== setId) return false;
    return filter === "all" || (filter === "removed") === !!item.removedAt;
}

/** Adds or replaces backgrounds in the order of the answer; new ones go to the end. */
function merge(items: readonly BackgroundResponseDto[], incoming: readonly BackgroundResponseDto[]) {
    const index = new Map(items.map((item, position) => [item.id, position]));
    const next = [...items];
    for (const item of incoming) {
        const position = index.get(item.id);
        if (position === undefined) {
            index.set(item.id, next.length);
            next.push(item);
        } else if (next[position].revision <= item.revision) next[position] = item;
    }
    return next;
}

/**
 * The gallery of a set: pages by cursor in the order of adding; everything loaded is kept and the virtual grid draws it.
 * No polling: backgrounds.changed with itemsChangedIn of this set rereads only the tail (new backgrounds go to the end), backgrounds of
 * explicit operations in the event are reread one by one, deleted ones are removed. A change of the set or the filter starts a new
 * generation: answers of the old one are dropped.
 */
export function useBackgroundGallery(projectId: string, setId: string | undefined, filter: BackgroundRemovedFilter) {
    const [state, setState] = useState<GalleryState>(emptyState);
    const generation = useRef(0);
    const mounted = useRef(true);
    /** The cursor the last page was loaded from (undefined: from the start). */
    const tailAfter = useRef<string | undefined>(undefined);
    const loadingMore = useRef<Promise<BackgroundResponseDto[]> | null>(null);
    const rereadingTail = useRef<Promise<void> | null>(null);
    const tailAgain = useRef(false);
    const latest = useRef(state);
    latest.current = state;
    const renewQueue = useRef(new Set<string>());
    const renewTimer = useRef<number | undefined>(undefined);

    const fail = useCallback((current: number, cause: unknown) => {
        if (mounted.current && current === generation.current) {
            setState(value => ({ ...value, loading: false, error: errorMessage(cause, i18n.t("backgrounds:errors.loadBackgrounds")) }));
        }
    }, []);

    const readPage = useCallback(async (current: number, after: string | undefined, limit: number, tail: boolean) => {
        if (!setId) return [];
        const page = await backgroundsApi.list({ projectId, setId, removed: filter, limit, after });
        if (!mounted.current || current !== generation.current) return [];
        // A tail re-read may return the cursor that ends the list again; the next page starts after its last item.
        if (!tail || page.items.length) tailAfter.current = after;
        setState(value => ({ ...value, items: merge(value.items, page.items), nextCursor: page.nextCursor, loading: false, error: undefined }));
        return page.items;
    }, [projectId, setId, filter]);

    const reload = useCallback(async () => {
        const current = ++generation.current;
        tailAfter.current = undefined;
        loadingMore.current = null;
        rereadingTail.current = null;
        setState({ ...emptyState, loading: !!setId });
        try {
            await readPage(current, undefined, backgroundPageSize, false);
        } catch (cause) {
            fail(current, cause);
        }
    }, [setId, readPage, fail]);

    /** The next page; a repeated call during loading waits for the same page. */
    const loadMore = useCallback((): Promise<BackgroundResponseDto[]> => {
        const after = latest.current.nextCursor;
        if (!after) return Promise.resolve([]);
        if (loadingMore.current) return loadingMore.current;
        const current = generation.current;
        setState(value => ({ ...value, loading: true }));
        const work = readPage(current, after, backgroundPageSize, false)
            .catch(cause => { fail(current, cause); return [] as BackgroundResponseDto[]; })
            .finally(() => { if (current === generation.current) loadingMore.current = null; });
        loadingMore.current = work;
        return work;
    }, [readPage, fail]);

    /**
     * New backgrounds of the set go to the end: the tail is reread when the list is loaded to the end. Preparation reports on the set
     * often, so reads do not overlap: an event during a read repeats it once.
     */
    const rereadTail = useCallback(() => {
        if (rereadingTail.current) {
            tailAgain.current = true;
            return;
        }
        const current = generation.current;
        const work = (async () => {
            try {
                do {
                    tailAgain.current = false;
                    if (latest.current.nextCursor !== null || loadingMore.current) return;
                    // A full tail page leaves nextCursor set: the grid then loads the rest as the user scrolls.
                    await readPage(current, tailAfter.current, tailLimit, true);
                } while (tailAgain.current && current === generation.current);
            } catch (cause) {
                fail(current, cause);
            } finally {
                if (current === generation.current) rereadingTail.current = null;
            }
        })();
        rereadingTail.current = work;
    }, [readPage, fail]);

    /** Rereads backgrounds one by one: deleted ones or ones out of the filter are removed. */
    const rereadItems = useCallback(async (ids: readonly string[]) => {
        if (!setId || !ids.length) return;
        const current = generation.current;
        const fresh: BackgroundResponseDto[] = [];
        const gone = new Set<string>();
        await Promise.all(ids.map(async itemId => {
            try {
                const item = await backgroundsApi.get({ projectId, itemId });
                if (matchesRemovedFilter(item, setId, filter)) fresh.push(item);
                else gone.add(itemId);
            } catch (cause) {
                if (cause instanceof BridgeError && cause.code === "backgrounds.item_not_found") gone.add(itemId);
            }
        }));
        if (!mounted.current || current !== generation.current) return;
        setState(value => ({ ...value, items: merge(value.items.filter(item => !gone.has(item.id)), fresh) }));
    }, [projectId, setId, filter]);

    useEffect(() => {
        mounted.current = true;
        void reload();
        return () => { mounted.current = false; };
    }, [reload]);

    useEffect(() => {
        if (!setId) return;
        return subscribeBackgroundsChanged(event => {
            if (event.data.projectId !== projectId) return;
            const known = new Map(latest.current.items.map(item => [item.id, item.revision]));
            const removed = new Set<string>();
            const changed: string[] = [];
            for (const change of event.data.items) {
                const revision = known.get(change.id);
                if (revision === undefined) continue;
                if (change.revision === null) removed.add(change.id);
                else if (change.revision > revision) changed.push(change.id);
            }
            if (removed.size) setState(value => ({ ...value, items: value.items.filter(item => !removed.has(item.id)) }));
            void rereadItems(changed.slice(0, 64));
            if (event.data.itemsChangedIn.includes(setId)) rereadTail();
        });
    }, [projectId, setId, rereadItems, rereadTail]);

    /** The answer of an operation: the background is replaced, or removed when it no longer matches the filter. */
    const applyItems = useCallback((items: readonly BackgroundResponseDto[]) => {
        if (!setId) return;
        const keep = items.filter(item => matchesRemovedFilter(item, setId, filter));
        const drop = new Set(items.filter(item => !matchesRemovedFilter(item, setId, filter)).map(item => item.id));
        setState(value => {
            const loaded = new Set(value.items.map(item => item.id));
            return { ...value, items: merge(value.items.filter(item => !drop.has(item.id)), keep.filter(item => loaded.has(item.id))) };
        });
    }, [setId, filter]);

    const removeItems = useCallback((ids: readonly string[]) => {
        const gone = new Set(ids);
        setState(value => ({ ...value, items: value.items.filter(item => !gone.has(item.id)) }));
    }, []);

    /** Expired thumbnail URLs: backgrounds that reported an error almost at once are reread in one batch. */
    const renew = useCallback((itemId: string) => {
        renewQueue.current.add(itemId);
        window.clearTimeout(renewTimer.current);
        renewTimer.current = window.setTimeout(() => {
            const ids = [...renewQueue.current];
            renewQueue.current.clear();
            const current = generation.current;
            void Promise.all(ids.map(id => backgroundsApi.get({ projectId, itemId: id }).catch(() => null))).then(items => {
                if (!mounted.current || current !== generation.current) return;
                setState(value => ({ ...value, items: merge(value.items, items.filter((item): item is BackgroundResponseDto => !!item && value.items.some(known => known.id === item.id))) }));
            });
        }, 50);
    }, [projectId]);

    useEffect(() => () => window.clearTimeout(renewTimer.current), []);

    return { ...state, hasMore: state.nextCursor !== null, reload, loadMore, applyItems, removeItems, renew };
}

export type BackgroundGalleryState = ReturnType<typeof useBackgroundGallery>;
