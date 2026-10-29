import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import "@/i18n/sections/catalog";

const delayMs = 400;

/**
 * The box color of a variant in annotations. While the picker is open the value lives locally; it is saved with one request 400 ms
 * after the last change, otherwise dragging in the picker becomes a stream of requests. A pending value is saved when the screen is
 * left as well. Reset returns the default color (null).
 */
export function VariantColorInput({ name, color, displayColor, disabled, save, label, resetText }: {
    name: string;
    color: string | null;
    displayColor: string;
    disabled: boolean;
    save: (color: string | null) => void;
    /** The field label instead of "Color of variant {name}". */
    label?: string;
    /** A text reset button instead of the icon. */
    resetText?: string;
}) {
    const { t } = useTranslation("catalog");
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
                title={t("color.hint")}
                aria-label={label ?? t("color.label", { name })}
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
                    aria-label={t("color.resetLabel", { name })}
                    title={t("color.reset")}
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
