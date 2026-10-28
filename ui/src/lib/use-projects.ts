import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeProjectChanged, subscribeProjectRemoved } from "@/api/events/subscriptions";
import { projectsApi } from "@/api/projects/projects-api";
import type { ProjectResponseDto } from "@/api/projects/response-dto/project-response-dto";
import { errorMessage } from "./format";

function upsertProject(items: readonly ProjectResponseDto[], project: ProjectResponseDto): ProjectResponseDto[] {
    const current = items.find(item => item.id === project.id);
    // An older response must not overwrite a newer revision that already arrived as an event.
    if (current && current.revision > project.revision) return [...items];
    return [...items.filter(item => item.id !== project.id), project]
        .sort((a, b) => b.createdAt.localeCompare(a.createdAt) || a.name.localeCompare(b.name, "ru"));
}

/**
 * Библиотека проектов: список запросом, создание, импорт и изменения — ответами и событиями project.changed, удаление — project.removed.
 * lastProjectId — последний открытый проект (null, если его нет).
 */
export function useProjects() {
    const [projects, setProjects] = useState<readonly ProjectResponseDto[]>([]);
    const [lastProjectId, setLastProjectId] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);

    const apply = useCallback((project: ProjectResponseDto) => setProjects(items => upsertProject(items, project)), []);

    /** Открытый проект становится последним (Core запоминает его в OpenAsync). */
    const opened = useCallback((project: ProjectResponseDto) => {
        apply(project);
        setLastProjectId(project.id);
    }, [apply]);

    const remove = useCallback((projectId: string) => {
        setProjects(items => items.filter(item => item.id !== projectId));
        setLastProjectId(last => last === projectId ? null : last);
    }, []);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const result = await projectsApi.list();
            if (!mounted.current) return;
            setProjects(result.items.reduce<ProjectResponseDto[]>((items, project) => upsertProject(items, project), []));
            setLastProjectId(result.lastProjectId);
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, "Не удалось загрузить проекты. Повторите загрузку."));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, []);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeProjectChanged(event => apply(event.data));
        const unsubscribeRemoved = subscribeProjectRemoved(event => remove(event.data.projectId));
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
            unsubscribeRemoved();
        };
    }, [apply, remove, refresh]);

    return { projects, lastProjectId, loading, error, refresh, apply, opened, remove };
}

export type ProjectsState = ReturnType<typeof useProjects>;
