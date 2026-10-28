import { useCallback, useEffect, useRef, useState } from "react";
import { generationApi } from "@/api/projects/generation/generation-api";
import { maxSamples } from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeFormDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import type { GenerationSampleResponseDto } from "@/api/projects/generation/response-dto/generation-sample-response-dto";
import { errorMessage } from "./format";
import { sampleKey } from "./generation/recipe-form";

interface SamplesState {
    samples: readonly GenerationSampleResponseDto[];
    /** Настройки и seed, по которым нарисованы кадры (sampleKey). */
    key?: string;
    seed?: number;
    loading: boolean;
    error?: string;
}

/**
 * «Пример кадра»: до 4 первых кадров запуска с настройками формы и seed без сохранения. Новый запрос отменяет прежний;
 * URL кадров временные, поэтому истёкший кадр запрашивает примеры заново (renew) по тем же настройкам.
 */
export function useGenerationSamples(projectId: string) {
    const [state, setState] = useState<SamplesState>({ samples: [], loading: false });
    const controller = useRef<AbortController | null>(null);
    const last = useRef<{ recipe: GenerationRecipeFormDto; seed: number } | null>(null);

    const request = useCallback(async (recipe: GenerationRecipeFormDto, seed: number) => {
        controller.current?.abort();
        const current = new AbortController();
        controller.current = current;
        last.current = { recipe, seed };
        setState(previous => ({ ...previous, loading: true, error: undefined }));
        try {
            const result = await generationApi.previewSamples({ projectId, recipe, seed, count: maxSamples }, current.signal);
            if (!current.signal.aborted) setState({ samples: result.samples, key: sampleKey(recipe, seed), seed, loading: false });
        } catch (cause) {
            if (!current.signal.aborted) setState({ samples: [], loading: false, error: errorMessage(cause, "Не удалось построить пример кадра. Повторите попытку.") });
        }
    }, [projectId]);

    const renew = useCallback(() => {
        if (last.current && !controller.current?.signal.aborted) void request(last.current.recipe, last.current.seed);
    }, [request]);

    const clear = useCallback(() => {
        controller.current?.abort();
        last.current = null;
        setState({ samples: [], loading: false });
    }, []);

    useEffect(() => () => controller.current?.abort(), []);

    return { ...state, request, renew, clear };
}

export type GenerationSamplesState = ReturnType<typeof useGenerationSamples>;
