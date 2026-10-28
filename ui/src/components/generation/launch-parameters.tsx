import { useId } from "react";
import { generationTexts, isValidCount, isValidSeed, maxSeed, randomSeed } from "@/api/projects/generation/generation-rules";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";

/** Значения «Параметров запуска»: NaN — поле пустое. */
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
 * «Параметры запуска»: «Количество изображений» и «Дополнительно» — seed и «Новый случайный seed». Число потоков не
 * задаётся: его определяет очередь задач, а результат с тем же seed от него не зависит.
 */
export function LaunchParameters({ values, setValues, advancedOpen, setAdvancedOpen }: {
    values: LaunchValues;
    setValues: (values: LaunchValues) => void;
    advancedOpen: boolean;
    setAdvancedOpen: (open: boolean) => void;
}) {
    const id = useId();
    const error = launchError(values);
    return (
        <section className="rounded-xl border bg-card p-5" aria-labelledby={`${id}-title`} data-testid="launch-parameters">
            <h2 id={`${id}-title`} className="mb-4 font-medium">Параметры запуска</h2>
            <div className="grid grid-cols-2 gap-3">
                <div className="space-y-2">
                    <label htmlFor={`${id}-count`} className="block text-xs text-muted-foreground">Количество изображений</label>
                    <Input id={`${id}-count`} type="number" min={1} step={1} data-testid="launch-count" aria-invalid={!isValidCount(values.count) || undefined}
                        value={Number.isFinite(values.count) ? values.count : ""} onChange={event => setValues({ ...values, count: parse(event.target.value) })} />
                </div>
            </div>
            <details className="mt-4 rounded-md border px-3 py-2" open={advancedOpen} onToggle={event => setAdvancedOpen(event.currentTarget.open)} data-testid="launch-advanced">
                <summary className="cursor-pointer text-sm text-muted-foreground select-none">Дополнительно</summary>
                <div className="mt-3 grid grid-cols-2 gap-3">
                    <div className="space-y-2">
                        <label htmlFor={`${id}-seed`} className="block text-xs text-muted-foreground">Случайность (seed)</label>
                        <Input id={`${id}-seed`} type="number" min={0} max={maxSeed} step={1} data-testid="launch-seed" aria-invalid={!isValidSeed(values.seed) || undefined}
                            value={Number.isFinite(values.seed) ? values.seed : ""} onChange={event => setValues({ ...values, seed: parse(event.target.value) })} />
                    </div>
                </div>
                <p className="mt-3 text-xs text-muted-foreground">
                    Seed выбирается случайно при открытии; укажите прежний, чтобы повторить запуск. С тем же seed результат одинаков: пример кадра показывает первые кадры запуска.
                </p>
                <Button type="button" variant="outline" size="sm" className="mt-3" data-testid="launch-new-seed" onClick={() => setValues({ ...values, seed: randomSeed() })}>
                    Новый случайный seed
                </Button>
            </details>
            <p className="mt-3 min-h-4 text-xs leading-4 text-destructive" aria-live="polite" data-testid="launch-hint">
                {error}
            </p>
            <p className="mt-3 text-xs text-muted-foreground">Каждый запуск создаёт отдельную группу. Рецепт и источники фиксируются на момент запуска.</p>
        </section>
    );
}
