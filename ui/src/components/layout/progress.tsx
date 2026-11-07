import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

interface ProgressProps extends Omit<ComponentProps<"div">, "role"> {
    /** Percent from 0 to 100; without a value the bar is indeterminate. */
    value?: number | null;
    /** The job runs right now: the fill shimmers. */
    live?: boolean;
}

/** A 4 px progress bar with the brand fill; name it with aria-label or aria-labelledby. */
export function Progress({ value, live = false, className, ...props }: ProgressProps) {
    const determinate = typeof value === "number" && Number.isFinite(value);
    const percent = determinate ? Math.min(100, Math.max(0, value)) : 0;
    return (
        <div
            role="progressbar"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={determinate ? Math.round(percent) : undefined}
            data-state={determinate ? "determinate" : "indeterminate"}
            className={cn("h-1 overflow-hidden rounded-full bg-ink-3", className)}
            {...props}
        >
            <div
                className={cn(
                    "h-full rounded-full bg-brand shadow-[0_0_12px_color-mix(in_srgb,var(--brand)_55%,transparent)]",
                    determinate ? "transition-[width] duration-420 ease-glass" : "w-2/5 animate-progress-sweep",
                    live && determinate && "bg-[linear-gradient(90deg,var(--brand),var(--brand-strong),var(--brand))] bg-size-[200%_100%] animate-progress-shimmer"
                )}
                style={determinate ? { width: `${percent}%` } : undefined}
            />
        </div>
    );
}
