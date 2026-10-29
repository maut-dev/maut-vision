import { blurError, blurSliderMax, defaultBlur, maxBlurRadius, maxBlurSeed } from "@/api/projects/backgrounds/background-import-rules";
import type { BackgroundBlurRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import "@/i18n/sections/backgrounds";
import { i18n } from "@/i18n";
import { formatNumber } from "@/lib/format";

/** The "Blur backgrounds" fields as the user typed them. */
export interface BlurFields {
    enabled: boolean;
    min: string;
    max: string;
    /** Empty: "From the operation". */
    seed: string;
}

export const defaultBlurFields: BlurFields = { enabled: false, min: String(defaultBlur.minRadius), max: String(defaultBlur.maxRadius), seed: "" };

export interface BlurForm {
    rangeError: string;
    seedError: string;
    error: string;
    /** The blur of the request; null when it is off or a field is wrong. */
    value: BackgroundBlurRequestDto | null;
}

/** Validates the fields with message texts; the result is checked with the same rule as the host (blurError). */
export function blurForm(fields: BlurFields): BlurForm {
    if (!fields.enabled) return { rangeError: "", seedError: "", error: "", value: null };
    const min = Number(fields.min), max = Number(fields.max);
    const rangeError = fields.min.trim() === "" || fields.max.trim() === "" || !Number.isFinite(min) || !Number.isFinite(max)
        ? i18n.t("backgrounds:blur.errors.numbers")
        : min < 0 || max > maxBlurRadius
            ? i18n.t("backgrounds:blur.errors.range", { max: maxBlurRadius })
            : min > max ? i18n.t("backgrounds:blur.errors.order") : "";
    const seedText = fields.seed.trim();
    const seedError = seedText !== "" && !(/^\d+$/.test(seedText) && Number(seedText) <= maxBlurSeed)
        ? i18n.t("backgrounds:blur.errors.seed")
        : "";
    const error = rangeError || seedError;
    if (error) return { rangeError, seedError, error, value: null };
    const value: BackgroundBlurRequestDto = { minRadius: min, maxRadius: max, seed: seedText ? Number(seedText) : null };
    const invalid = i18n.t("backgrounds:blur.errors.range", { max: maxBlurRadius });
    return blurError(value) ? { rangeError: invalid, seedError, error: invalid, value: null } : { rangeError, seedError, error, value };
}

/** Thumb positions 0–32 px: values above the slider limit sit at its end, the inputs take up to 1000. */
export function sliderRange(fields: BlurFields): [number, number] {
    const clamp = (value: number) => Math.min(blurSliderMax, Math.max(0, Number.isFinite(value) ? value : 0));
    const min = clamp(Number(fields.min));
    return [min, Math.max(min, clamp(Number(fields.max)))];
}

/** "1,5" / "1.5": a radius to hundredths. */
export function formatRadius(value: number): string {
    return formatNumber(value, { maximumFractionDigits: 2 });
}
