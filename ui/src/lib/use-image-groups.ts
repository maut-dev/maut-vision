import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeImagesChanged } from "@/api/events/subscriptions";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageGroupResponseDto } from "@/api/projects/images/response-dto/image-group-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { errorMessage } from "./format";

const byCreation = (left: ImageGroupResponseDto, right: ImageGroupResponseDto) =>
    left.createdAt.localeCompare(right.createdAt) || left.id.localeCompare(right.id);

/**
 * Группы изображений открытого проекта: images.listGroups при открытии, затем images.changed. Перечитываются только
 * названные событием группы (новая ревизия, удаление или изменение состава и счётчиков); ответ, начатый раньше
 * следующего чтения той же группы, отбрасывается.
 */
export function useImageGroups(projectId: string) {
    const [groups, setGroups] = useState<readonly ImageGroupResponseDto[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const reads = useRef(new Map<string, number>());
    const sequence = useRef(0);

    const remove = useCallback((groupId: string) => {
        setGroups(current => current.filter(item => item.id !== groupId));
    }, []);

    /** Ответ операции или чтения; более старая ревизия не заменяет новую. */
    const apply = useCallback((group: ImageGroupResponseDto) => {
        setGroups(current => {
            const known = current.find(item => item.id === group.id);
            if (known && known.revision > group.revision) return current;
            return known ? current.map(item => item.id === group.id ? group : item) : [...current, group].sort(byCreation);
        });
    }, []);

    const reread = useCallback(async (groupId: string) => {
        const read = ++sequence.current;
        reads.current.set(groupId, read);
        try {
            const group = await imagesApi.getGroup({ projectId, groupId });
            if (mounted.current && reads.current.get(groupId) === read) apply(group);
        } catch (cause) {
            if (!mounted.current || reads.current.get(groupId) !== read) return;
            if (cause instanceof BridgeError && cause.code === "images.group_not_found") remove(groupId);
        }
    }, [projectId, apply, remove]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const list = await imagesApi.listGroups({ projectId });
            if (!mounted.current) return;
            setGroups([...list].sort(byCreation));
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, "Не удалось загрузить группы изображений. Повторите загрузку."));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeImagesChanged(event => {
            if (event.data.projectId !== projectId) return;
            const ids = new Set(event.data.imagesChangedIn);
            for (const change of event.data.groups) {
                if (change.revision === null) {
                    ids.delete(change.id);
                    reads.current.delete(change.id);
                    remove(change.id);
                } else ids.add(change.id);
            }
            for (const id of ids) void reread(id);
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh, reread, remove]);

    return { groups, loading, error, refresh, apply, remove, reread };
}

export type ImageGroupsState = ReturnType<typeof useImageGroups>;
