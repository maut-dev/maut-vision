import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeImagesChanged } from "@/api/events/subscriptions";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageArchiveFilter } from "@/api/projects/images/request-dto/list-images-request-dto";
import type { ImageAnnotationResponseDto } from "@/api/projects/images/response-dto/image-annotation-response-dto";
import type { ImageResponseDto, ImageReviewState } from "@/api/projects/images/response-dto/image-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { errorMessage } from "./format";

export interface GalleryFilter {
    archive: ImageArchiveFilter;
    /** null — любые. */
    reviewState: ImageReviewState | null;
}

export const galleryPageSize = 100;
/** Предел страницы и пачки разметки в API. */
const batchLimit = 200;

interface GalleryState {
    items: readonly ImageResponseDto[];
    annotations: Readonly<Record<string, ImageAnnotationResponseDto>>;
    /** Курсор следующей страницы; null — загружено до конца. */
    nextCursor: string | null;
    loading: boolean;
    error?: string;
}

const emptyState: GalleryState = { items: [], annotations: {}, nextCursor: null, loading: false };

export function matchesFilter(image: ImageResponseDto, groupId: string, filter: GalleryFilter): boolean {
    if (image.groupId !== groupId) return false;
    if (filter.archive === "active" && image.archivedAt) return false;
    if (filter.archive === "archived" && !image.archivedAt) return false;
    return filter.reviewState === null || image.reviewState === filter.reviewState;
}

function chunks<T>(items: readonly T[], size: number): T[][] {
    const result: T[][] = [];
    for (let index = 0; index < items.length; index += size) result.push(items.slice(index, index + size));
    return result;
}

/**
 * Галерея группы: страницы по курсору в порядке импорта и разметка каждой страницы для рамок на миниатюрах.
 * Загруженное хранится целиком, а рисует его виртуальная сетка. Без опроса: images.changed с imagesChangedIn этой
 * группы перечитывает только хвост (страницу от курсора последней загрузки, туда встают новые кадры), изменённые
 * изображения из события перечитываются поштучно. Смена группы или фильтра начинает новое поколение: ответы старого
 * отбрасываются.
 */
export function useImageGallery(projectId: string, groupId: string | undefined, filter: GalleryFilter) {
    const [state, setState] = useState<GalleryState>(emptyState);
    const generation = useRef(0);
    const mounted = useRef(true);
    /** Курсор, с которого загружена последняя страница (undefined — с начала). */
    const tailAfter = useRef<string | undefined>(undefined);
    const loadingMore = useRef<Promise<ImageResponseDto[]> | null>(null);
    const latest = useRef(state);
    latest.current = state;
    const renewQueue = useRef(new Set<string>());
    const renewTimer = useRef<number | undefined>(undefined);
    const filterKey = `${filter.archive}:${filter.reviewState ?? "any"}`;
    const filterRef = useRef(filter);
    filterRef.current = filter;

    const fail = useCallback((current: number, cause: unknown) => {
        if (mounted.current && current === generation.current) {
            setState(value => ({ ...value, loading: false, error: errorMessage(cause, "Не удалось загрузить изображения. Повторите загрузку.") }));
        }
    }, []);

    const loadAnnotations = useCallback(async (current: number, ids: readonly string[]) => {
        for (const part of chunks(ids, batchLimit)) {
            const list = await imagesApi.listAnnotations({ projectId, imageIds: part });
            if (!mounted.current || current !== generation.current) return;
            setState(value => {
                const annotations = { ...value.annotations };
                for (const item of list) annotations[item.imageId] = item;
                return { ...value, annotations };
            });
        }
    }, [projectId]);

    /** Добавляет или заменяет изображения в порядке ответа; новые встают в конец. */
    const merge = useCallback((items: readonly ImageResponseDto[], incoming: readonly ImageResponseDto[]) => {
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
    }, []);

    const readPage = useCallback(async (current: number, after: string | undefined, limit: number, tail: boolean) => {
        if (!groupId) return [];
        const active = filterRef.current;
        const page = await imagesApi.list({ projectId, groupId, archive: active.archive, reviewState: active.reviewState, limit, after });
        if (!mounted.current || current !== generation.current) return [];
        // A tail re-read may return the cursor that ends the list again; the next page starts after its last item.
        if (!tail || page.items.length) tailAfter.current = after;
        setState(value => ({ ...value, items: merge(value.items, page.items), nextCursor: page.nextCursor, loading: false, error: undefined }));
        void loadAnnotations(current, page.items.map(item => item.id)).catch(cause => fail(current, cause));
        return page.items;
    }, [projectId, groupId, merge, loadAnnotations, fail]);

    const reload = useCallback(async () => {
        const current = ++generation.current;
        tailAfter.current = undefined;
        loadingMore.current = null;
        setState({ ...emptyState, loading: !!groupId });
        try {
            await readPage(current, undefined, galleryPageSize, false);
        } catch (cause) {
            fail(current, cause);
        }
    }, [groupId, readPage, fail]);

    /** Следующая страница; повторный вызов во время загрузки ждёт ту же страницу. */
    const loadMore = useCallback((): Promise<ImageResponseDto[]> => {
        const after = latest.current.nextCursor;
        if (!after) return Promise.resolve([]);
        if (loadingMore.current) return loadingMore.current;
        const current = generation.current;
        setState(value => ({ ...value, loading: true }));
        const work = readPage(current, after, galleryPageSize, false)
            .catch(cause => { fail(current, cause); return [] as ImageResponseDto[]; })
            .finally(() => { if (current === generation.current) loadingMore.current = null; });
        loadingMore.current = work;
        return work;
    }, [readPage, fail]);

    /** Новые кадры группы встают в конец: перечитывается хвост, если список уже загружен до конца. */
    const rereadTail = useCallback(async () => {
        if (latest.current.nextCursor !== null || loadingMore.current) return;
        const current = generation.current;
        try {
            await readPage(current, tailAfter.current, batchLimit, true);
        } catch (cause) {
            fail(current, cause);
        }
    }, [readPage, fail]);

    /** Перечитывает изображения поштучно: удалённые, ушедшие в другую группу или из фильтра убираются. */
    const rereadImages = useCallback(async (ids: readonly string[]) => {
        if (!groupId || !ids.length) return;
        const current = generation.current;
        const fresh: ImageResponseDto[] = [];
        const gone = new Set<string>();
        for (const part of chunks(ids, 16)) {
            await Promise.all(part.map(async imageId => {
                try {
                    const image = await imagesApi.get({ projectId, imageId });
                    if (matchesFilter(image, groupId, filterRef.current)) fresh.push(image);
                    else gone.add(imageId);
                } catch (cause) {
                    if (cause instanceof BridgeError && cause.code === "images.image_not_found") gone.add(imageId);
                }
            }));
            if (!mounted.current || current !== generation.current) return;
        }
        setState(value => ({ ...value, items: merge(value.items.filter(item => !gone.has(item.id)), fresh) }));
        await loadAnnotations(current, fresh.map(item => item.id)).catch(cause => fail(current, cause));
    }, [projectId, groupId, merge, loadAnnotations, fail]);

    useEffect(() => {
        mounted.current = true;
        void reload();
        return () => { mounted.current = false; };
    }, [reload, filterKey]);

    useEffect(() => {
        if (!groupId) return;
        return subscribeImagesChanged(event => {
            if (event.data.projectId !== projectId) return;
            const known = new Map(latest.current.items.map(item => [item.id, item.revision]));
            const removed = new Set<string>();
            const changed: string[] = [];
            for (const change of event.data.images) {
                const revision = known.get(change.id);
                if (revision === undefined) continue;
                if (change.revision === null) removed.add(change.id);
                else if (change.revision > revision) changed.push(change.id);
            }
            if (removed.size) setState(value => ({ ...value, items: value.items.filter(item => !removed.has(item.id)) }));
            void rereadImages(changed);
            if (event.data.imagesChangedIn.includes(groupId)) void rereadTail();
        });
    }, [projectId, groupId, rereadImages, rereadTail]);

    /** Ответ операции: изображение заменяется, или убирается, если больше не подходит под фильтр. */
    const applyImages = useCallback((images: readonly ImageResponseDto[]) => {
        if (!groupId) return;
        const filterNow = filterRef.current;
        const keep = images.filter(image => matchesFilter(image, groupId, filterNow));
        const drop = new Set(images.filter(image => !matchesFilter(image, groupId, filterNow)).map(image => image.id));
        setState(value => {
            const loaded = new Set(value.items.map(item => item.id));
            return { ...value, items: merge(value.items.filter(item => !drop.has(item.id)), keep.filter(image => loaded.has(image.id))) };
        });
    }, [groupId, merge]);

    const applyAnnotation = useCallback((annotation: ImageAnnotationResponseDto) => {
        setState(value => {
            const known = value.annotations[annotation.imageId];
            if (known && known.revision > annotation.revision) return value;
            return { ...value, annotations: { ...value.annotations, [annotation.imageId]: annotation } };
        });
    }, []);

    const removeImages = useCallback((ids: readonly string[]) => {
        const gone = new Set(ids);
        setState(value => ({ ...value, items: value.items.filter(item => !gone.has(item.id)) }));
    }, []);

    /** Истёкшие URL миниатюр: изображения, сообщившие об ошибке почти одновременно, перечитываются одной пачкой. */
    const renew = useCallback((imageId: string) => {
        renewQueue.current.add(imageId);
        window.clearTimeout(renewTimer.current);
        renewTimer.current = window.setTimeout(() => {
            const ids = [...renewQueue.current];
            renewQueue.current.clear();
            const current = generation.current;
            void Promise.all(ids.map(id => imagesApi.get({ projectId, imageId: id }).catch(() => null))).then(images => {
                if (!mounted.current || current !== generation.current) return;
                setState(value => ({ ...value, items: merge(value.items, images.filter((image): image is ImageResponseDto => !!image && value.items.some(item => item.id === image.id))) }));
            });
        }, 50);
    }, [projectId, merge]);

    useEffect(() => () => window.clearTimeout(renewTimer.current), []);

    return { ...state, hasMore: state.nextCursor !== null, reload, loadMore, applyImages, applyAnnotation, removeImages, renew };
}

export type ImageGalleryState = ReturnType<typeof useImageGallery>;
