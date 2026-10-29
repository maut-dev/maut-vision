import { useCallback, useEffect, useRef, useState } from "react";
import { classesApi } from "@/api/projects/catalog/classes-api";
import type { CatalogWorkspaceResponseDto } from "@/api/projects/catalog/response-dto/catalog-workspace-response-dto";
import type { ClassOrderResponseDto, ClassResponseDto } from "@/api/projects/catalog/response-dto/class-response-dto";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import type { VariantResponseDto } from "@/api/projects/catalog/response-dto/variant-response-dto";
import { subscribeCatalogChanged } from "@/api/events/subscriptions";
import { i18n } from "@/i18n";
import "@/i18n/sections/catalog";
import { errorMessage } from "./format";

interface Revisioned {
    id: string;
    revision: number;
}

/** The answer of an operation must not overwrite a newer revision that already came with a snapshot. */
function upsert<T extends Revisioned>(items: readonly T[], item: T, order?: (left: T, right: T) => number): T[] {
    const current = items.find(entry => entry.id === item.id);
    if (current && current.revision > item.revision) return [...items];
    const next = current ? items.map(entry => entry.id === item.id ? item : entry) : [...items, item];
    return order ? next.sort(order) : next;
}

const bySortOrder = (left: { sortOrder: number; id: string }, right: { sortOrder: number; id: string }) =>
    left.sortOrder - right.sortOrder || left.id.localeCompare(right.id);

/**
 * The catalog of the open project: the classes.getWorkspace snapshot, then catalog.changed events.
 * The snapshot is reread only when the catalogRevision of an event is newer than the known one; requests do not run in parallel, and
 * a burst of events during a read gives one more read. Answers of operations apply at once so the screen does not wait for the event.
 */
export function useCatalog(projectId: string) {
    const [workspace, setWorkspace] = useState<CatalogWorkspaceResponseDto>();
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const known = useRef(0);
    const reading = useRef(false);
    const again = useRef(false);
    const mounted = useRef(true);

    const refresh = useCallback(async () => {
        if (reading.current) {
            again.current = true;
            return;
        }
        reading.current = true;
        try {
            do {
                again.current = false;
                try {
                    const snapshot = await classesApi.getWorkspace({ projectId });
                    if (!mounted.current) return;
                    if (snapshot.catalogRevision >= known.current) {
                        known.current = snapshot.catalogRevision;
                        setWorkspace(snapshot);
                    }
                    setError(undefined);
                } catch (cause) {
                    if (mounted.current) setError(errorMessage(cause, i18n.t("catalog:errors.loadCatalog")));
                }
            } while (again.current && mounted.current);
        } finally {
            reading.current = false;
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeCatalogChanged(event => {
            if (event.data.projectId === projectId && event.data.catalogRevision > known.current) void refresh();
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh]);

    const applyVariant = useCallback((variant: VariantResponseDto) => {
        setWorkspace(current => current && { ...current, variants: upsert(current.variants, variant, bySortOrder) });
    }, []);

    const applyClass = useCallback((item: ClassResponseDto) => {
        setWorkspace(current => current && { ...current, classes: upsert(current.classes, item, bySortOrder) });
    }, []);

    const applyClassVariant = useCallback((binding: ClassVariantResponseDto) => {
        setWorkspace(current => current && { ...current, classVariants: upsert(current.classVariants, binding) });
    }, []);

    const applyOrder = useCallback((order: ClassOrderResponseDto) => {
        setWorkspace(current => current && current.classOrderRevision <= order.orderRevision
            ? { ...current, classes: [...order.classes].sort(bySortOrder), classOrderRevision: order.orderRevision }
            : current);
    }, []);

    return { workspace, loading, error, refresh, applyVariant, applyClass, applyClassVariant, applyOrder };
}

export type CatalogState = ReturnType<typeof useCatalog>;
