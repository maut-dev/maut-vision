import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { blurSliderMax, blurStep, maxBlurRadius } from "@/api/projects/backgrounds/background-import-rules";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Slider } from "@/components/ui/slider";
import { sliderRange } from "@/lib/backgrounds/blur-form";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import "@/i18n/sections/backgrounds";

export function Field({ label, children }: { label: string; children: ReactNode }) {
    return (
        <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">{label}</span>
            {children}
        </label>
    );
}

/**
 * "Blur backgrounds", shared by all ways of adding. The slider covers 0–32 px (step 0.5), the inputs take up to 1000 px; the seed is
 * under "Advanced", an empty field means "From the operation".
 */
export function BackgroundBlurSettings({ blur, disabled }: { blur: BackgroundBlurState; disabled?: boolean }) {
    const { t } = useTranslation("backgrounds");
    const { fields } = blur;
    return (
        <section className="space-y-3 rounded-md border bg-card p-3" aria-label={t("blur.title")} data-testid="blur-settings">
            <label className="flex items-center gap-2 text-sm font-medium">
                <Checkbox disabled={disabled} checked={fields.enabled} data-testid="blur-enabled" onCheckedChange={checked => blur.change({ enabled: checked === true })} />
                {t("blur.enabled")}
            </label>
            <p className="text-xs text-muted-foreground">
                {t("blur.description")}
            </p>
            {fields.enabled && (
                <div className="space-y-3">
                    <div className="grid gap-3 sm:grid-cols-[8rem_8rem_minmax(0,1fr)] sm:items-end">
                        <Field label={t("blur.min")}>
                            <Input disabled={disabled} type="number" min={0} max={maxBlurRadius} step={blurStep} aria-invalid={!!blur.rangeError} data-testid="blur-min"
                                value={fields.min} onChange={event => blur.change({ min: event.target.value })} />
                        </Field>
                        <Field label={t("blur.max")}>
                            <Input disabled={disabled} type="number" min={0} max={maxBlurRadius} step={blurStep} aria-invalid={!!blur.rangeError} data-testid="blur-max"
                                value={fields.max} onChange={event => blur.change({ max: event.target.value })} />
                        </Field>
                        <div className="space-y-2 pb-2">
                            <Slider
                                aria-label={t("blur.range")}
                                thumbLabels={[t("blur.from"), t("blur.to")]}
                                disabled={disabled}
                                min={0}
                                max={blurSliderMax}
                                step={blurStep}
                                minStepsBetweenThumbs={0}
                                value={sliderRange(fields)}
                                onValueChange={values => blur.change({ min: String(values[0]), max: String(values[1]) })}
                            />
                            <div className="flex justify-between text-xs text-muted-foreground">
                                <span>0</span>
                                <span>{blurSliderMax} px</span>
                            </div>
                        </div>
                    </div>
                    <details className="rounded-md border px-3 py-2">
                        <summary className="cursor-pointer text-sm text-muted-foreground select-none">{t("advanced")}</summary>
                        <div className="mt-3 grid gap-3 sm:grid-cols-[12rem_minmax(0,1fr)] sm:items-end">
                            <Field label={t("blur.seed")}>
                                <Input disabled={disabled} inputMode="numeric" placeholder={t("blur.seedPlaceholder")} aria-invalid={!!blur.seedError} data-testid="blur-seed"
                                    value={fields.seed} onChange={event => blur.change({ seed: event.target.value })} />
                            </Field>
                            <p className="pb-2 text-xs text-muted-foreground">
                                {t("blur.seedHint")}
                            </p>
                        </div>
                    </details>
                </div>
            )}
            {blur.error && <p role="alert" className="text-xs text-destructive" data-testid="blur-error">{blur.error}</p>}
        </section>
    );
}
