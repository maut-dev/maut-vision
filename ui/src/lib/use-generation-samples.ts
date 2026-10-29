import { useCallback, useEffect, useRef, useState } from "react";
import { generationApi } from "@/api/projects/generation/generation-api";
import { maxSamples } from "@/api/projects/generation/generation-rules";
import type { GenerationRecipeFormDto } from "@/api/projects/generation/request-dto/generation-recipe-form-dto";
import type { GenerationSampleResponseDto } from "@/api/projects/generation/response-dto/generation-sample-response-dto";
import { i18n } from "@/i18n";
import { errorMessage } from "./format";
import "./generation/locale";
import { sampleKey } from "./generation/recipe-form";

interface SamplesState {
    samples: readonly GenerationSampleResponseDto[];
    /** Settings and seed the frames were drawn with (sampleKey). */
    key?: string;
    seed?: number;
    loading: boolean;
    error?: string;
}

/**
 * "Sample frame": up to 4 first frames of a run with the form settings and seed, without saving. A new request cancels the previous one;
 * frame URLs are temporary, so an expired frame requests the samples again (renew) with the same settings.
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
            if (!current.signal.aborted) setState({ samples: [], loading: false, error: errorMessage(cause, i18n.t("generation:loadErrors.samples")) });
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
