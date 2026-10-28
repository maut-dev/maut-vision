import { blurError, blurSliderMax, defaultBlur, maxBlurRadius, maxBlurSeed } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundBlurRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";

/** Поля «Размывать фоны» как их ввёл пользователь. */
export interface BlurFields {
    enabled: boolean;
    min: string;
    max: string;
    /** Пусто — «Из операции». */
    seed: string;
}

export const defaultBlurFields: BlurFields = { enabled: false, min: String(defaultBlur.minRadius), max: String(defaultBlur.maxRadius), seed: "" };

export interface BlurForm {
    rangeError: string;
    seedError: string;
    error: string;
    /** Размытие запроса; null — выключено или поля с ошибкой. */
    value: BackgroundBlurRequestDto | null;
}

/** Проверка полей с текстами сообщений; итог сверяется тем же правилом, что у host (blurError). */
export function blurForm(fields: BlurFields): BlurForm {
    if (!fields.enabled) return { rangeError: "", seedError: "", error: "", value: null };
    const min = Number(fields.min), max = Number(fields.max);
    const rangeError = fields.min.trim() === "" || fields.max.trim() === "" || !Number.isFinite(min) || !Number.isFinite(max)
        ? "Укажите радиус «от» и «до» числами."
        : min < 0 || max > maxBlurRadius
            ? `Радиус — от 0 до ${maxBlurRadius} px.`
            : min > max ? "Радиус «от» не может быть больше радиуса «до»." : "";
    const seedText = fields.seed.trim();
    const seedError = seedText !== "" && !(/^\d+$/.test(seedText) && Number(seedText) <= maxBlurSeed)
        ? "Дополнительно: seed — целое число от 0 до 4 294 967 295 или пустое поле."
        : "";
    const error = rangeError || seedError;
    if (error) return { rangeError, seedError, error, value: null };
    const value: BackgroundBlurRequestDto = { minRadius: min, maxRadius: max, seed: seedText ? Number(seedText) : null };
    return blurError(value) ? { rangeError: "Радиус — от 0 до 1000 px.", seedError, error: "Радиус — от 0 до 1000 px.", value: null } : { rangeError, seedError, error, value };
}

/** Положение бегунков 0–32 px: значения больше предела ползунка стоят у его конца, поля ввода принимают до 1000. */
export function sliderRange(fields: BlurFields): [number, number] {
    const clamp = (value: number) => Math.min(blurSliderMax, Math.max(0, Number.isFinite(value) ? value : 0));
    const min = clamp(Number(fields.min));
    return [min, Math.max(min, clamp(Number(fields.max)))];
}

/** «1,5» — радиус до сотых. */
export function formatRadius(value: number): string {
    return value.toLocaleString("ru-RU", { maximumFractionDigits: 2 });
}
