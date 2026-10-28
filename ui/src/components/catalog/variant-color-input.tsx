import { useEffect, useRef, useState } from "react";
import { RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";

const delayMs = 400;

/**
 * Цвет рамок варианта в разметке. Пока палитра открыта, значение живёт локально; сохранение уходит одним запросом
 * через 400 мс после последнего изменения, иначе перетаскивание в палитре превращается в поток запросов.
 * Отложенное значение сохраняется и при уходе с экрана. Сброс возвращает цвет по умолчанию (null).
 */
export function VariantColorInput({ name, color, displayColor, disabled, save, label, resetText }: {
    name: string;
    color: string | null;
    displayColor: string;
    disabled: boolean;
    save: (color: string | null) => void;
    /** Подпись поля вместо «Цвет варианта {name}». */
    label?: string;
    /** Текстовая кнопка сброса вместо значка. */
    resetText?: string;
}) {
    const [draft, setDraft] = useState<string>();
    const timer = useRef<number | undefined>(undefined);
    const pending = useRef<string | undefined>(undefined);
    const latest = useRef({ displayColor, save });
    latest.current = { displayColor, save };

    function flush() {
        window.clearTimeout(timer.current);
        const value = pending.current;
        pending.current = undefined;
        setDraft(undefined);
        if (value !== undefined && value !== latest.current.displayColor) latest.current.save(value);
    }

    useEffect(() => () => {
        window.clearTimeout(timer.current);
        const value = pending.current;
        if (value !== undefined && value !== latest.current.displayColor) latest.current.save(value);
    }, []);

    return (
        <span className="flex items-center gap-0.5">
            <input
                type="color"
                title="Цвет рамок этого варианта в разметке"
                aria-label={label ?? `Цвет варианта ${name}`}
                className={`${resetText ? "h-8 w-10 p-1" : "h-6 w-8 p-0"} cursor-pointer rounded border bg-transparent disabled:cursor-not-allowed disabled:opacity-50`}
                disabled={disabled}
                value={draft ?? displayColor}
                onChange={event => {
                    const value = event.target.value.toLowerCase();
                    setDraft(value);
                    pending.current = value;
                    window.clearTimeout(timer.current);
                    timer.current = window.setTimeout(flush, delayMs);
                }}
            />
            {color !== null && (
                <Button
                    size={resetText ? "sm" : "icon-xs"}
                    variant="ghost"
                    aria-label={`Сбросить цвет: ${name}`}
                    title="Вернуть цвет по умолчанию"
                    disabled={disabled}
                    onClick={() => {
                        window.clearTimeout(timer.current);
                        pending.current = undefined;
                        setDraft(undefined);
                        save(null);
                    }}
                >
                    {resetText ?? <RotateCcw />}
                </Button>
            )}
        </span>
    );
}
