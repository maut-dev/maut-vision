import { useId } from "react";
import { useTranslation } from "react-i18next";
import { generationTexts, isValidCount, isValidSeed, maxSeed, randomSeed } from "@/api/projects/generation/generation-rules";
import { Collapsible } from "@/components/layout/collapsible";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

/** Values of "Run parameters": NaN — the field is empty. */
export interface LaunchValues {
    count: number;
    seed: number;
}

export function launchError(values: LaunchValues): string | null {
    if (!isValidCount(values.count)) return generationTexts.countInvalid;
    if (!isValidSeed(values.seed)) return generationTexts.seedInvalid;
    return null;
}

const parse = (value: string) => value === "" ? Number.NaN : Number(value);

/**
 * "Run parameters": "Number of images" and "Advanced" — seed and "New random seed". The thread count is not set: the job queue
 * decides it, and the result with the same seed does not depend on it. "Advanced" stays mounted while closed: the seed field is the
 * seed of the sample frames as well.
 */
export function LaunchParameters({ values, setValues, advancedOpen, setAdvancedOpen }: {
    values: LaunchValues;
    setValues: (values: LaunchValues) => void;
    advancedOpen: boolean;
    setAdvancedOpen: (open: boolean) => void;
}) {
    const { t } = useTranslation("generation");
    const id = useId();
    const error = launchError(values);
    return (
        <section className="surface shrink-0 space-y-2 p-4" aria-labelledby={`${id}-title`} data-testid="launch-parameters">
            <div className="flex flex-wrap items-end gap-x-4 gap-y-2">
                <h2 id={`${id}-title`} className="min-w-0 flex-1 text-[15px] font-semibold tracking-[-0.015em]">{t("launch.title")}</h2>
                <label className="flex items-center gap-2 text-xs text-text-2" htmlFor={`${id}-count`}>
                    {t("launch.count")}
                    <Input id={`${id}-count`} type="number" min={1} step={1} className="w-28 tabular-nums" data-testid="launch-count" aria-invalid={!isValidCount(values.count) || undefined}
                        value={Number.isFinite(values.count) ? values.count : ""} onChange={event => setValues({ ...values, count: parse(event.target.value) })} />
                </label>
            </div>
            <p className="text-xs text-text-2">{t("launch.note")}</p>
            <Collapsible title={t("launch.advanced")} open={advancedOpen} onOpenChange={setAdvancedOpen} keepMounted className="-mx-3" data-testid="launch-advanced">
                <div className="space-y-2 px-3 pt-1 pb-1">
                    <div className="flex flex-wrap items-end gap-3">
                        <div className="space-y-2">
                            <label htmlFor={`${id}-seed`} className="block text-xs text-text-2">{t("launch.seed")}</label>
                            <Input id={`${id}-seed`} type="number" min={0} max={maxSeed} step={1} className="w-40 tabular-nums" data-testid="launch-seed"
                                aria-invalid={!isValidSeed(values.seed) || undefined}
                                value={Number.isFinite(values.seed) ? values.seed : ""} onChange={event => setValues({ ...values, seed: parse(event.target.value) })} />
                        </div>
                        <Button type="button" variant="outline" size="sm" data-testid="launch-new-seed" onClick={() => setValues({ ...values, seed: randomSeed() })}>
                            {t("launch.newSeed")}
                        </Button>
                    </div>
                    <p className="text-xs text-text-2">{t("launch.seedHint")}</p>
                </div>
            </Collapsible>
            <p className="min-h-4 text-xs leading-4 text-destructive" aria-live="polite" data-testid="launch-hint">
                {error}
            </p>
        </section>
    );
}
