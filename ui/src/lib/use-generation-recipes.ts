import { useCallback, useEffect, useRef, useState } from "react";
import { subscribeGenerationChanged } from "@/api/events/subscriptions";
import { generationApi } from "@/api/projects/generation/generation-api";
import type { GenerationRecipeResponseDto } from "@/api/projects/generation/response-dto/generation-recipe-response-dto";
import { BridgeError } from "@/api/transport/errors";
import { errorMessage } from "./format";

const newestFirst = (left: GenerationRecipeResponseDto, right: GenerationRecipeResponseDto) =>
    right.createdAt.localeCompare(left.createdAt) || right.id.localeCompare(left.id);

/**
 * Пресеты генерации открытого проекта (включая архивные): generation.listRecipes при открытии, затем generation.changed —
 * перечитываются только названные пресеты с ревизией новее известной, удалённые (revision null) убираются. Ответ операции
 * применяется сразу; более старая ревизия не заменяет новую.
 */
export function useGenerationRecipes(projectId: string) {
    const [recipes, setRecipes] = useState<readonly GenerationRecipeResponseDto[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string>();
    const mounted = useRef(true);
    const latest = useRef(recipes);
    latest.current = recipes;

    const apply = useCallback((recipe: GenerationRecipeResponseDto) => {
        setRecipes(current => {
            const known = current.find(item => item.id === recipe.id);
            if (known && known.revision > recipe.revision) return current;
            return known ? current.map(item => item.id === recipe.id ? recipe : item) : [...current, recipe].sort(newestFirst);
        });
    }, []);

    const remove = useCallback((recipeId: string) => {
        setRecipes(current => current.filter(item => item.id !== recipeId));
    }, []);

    const reread = useCallback(async (recipeId: string) => {
        try {
            const recipe = await generationApi.getRecipe({ projectId, recipeId });
            if (mounted.current) apply(recipe);
        } catch (cause) {
            if (mounted.current && cause instanceof BridgeError && cause.code === "generation.unavailable") remove(recipeId);
        }
    }, [projectId, apply, remove]);

    const refresh = useCallback(async () => {
        setLoading(true);
        try {
            const list = await generationApi.listRecipes({ projectId });
            if (!mounted.current) return;
            setRecipes([...list].sort(newestFirst));
            setError(undefined);
        } catch (cause) {
            if (mounted.current) setError(errorMessage(cause, "Не удалось загрузить пресеты генерации. Повторите загрузку."));
        } finally {
            if (mounted.current) setLoading(false);
        }
    }, [projectId]);

    useEffect(() => {
        mounted.current = true;
        const unsubscribe = subscribeGenerationChanged(event => {
            if (event.data.projectId !== projectId) return;
            for (const change of event.data.recipes) {
                if (change.revision === null) remove(change.id);
                else if ((latest.current.find(item => item.id === change.id)?.revision ?? 0) < change.revision) void reread(change.id);
            }
        });
        void refresh();
        return () => {
            mounted.current = false;
            unsubscribe();
        };
    }, [projectId, refresh, reread, remove]);

    return { recipes, loading, error, refresh, apply, remove, reread };
}

export type GenerationRecipesState = ReturnType<typeof useGenerationRecipes>;
