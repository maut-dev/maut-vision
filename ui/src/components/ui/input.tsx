import * as React from "react"
import { cn } from "@/lib/utils"

// Fields stay opaque (ink-2): text on glass loses contrast over light parts of the canvas.
const fieldClasses =
    "rounded-md border border-input bg-ink-2 text-[13px] text-text-1 caret-brand transition-[border-color,box-shadow] duration-120 ease-glass outline-none placeholder:text-text-3 hover:border-text-1/30 focus-visible:border-focus focus-visible:ring-[3px] focus-visible:ring-focus/30 disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 aria-invalid:border-danger aria-invalid:ring-danger/20"

function Input({ className, type, ...props }: React.ComponentProps<"input">) {
    return (
        <input
            type={type}
            data-slot="input"
            className={cn(
                "h-9 w-full min-w-0 px-3 py-1 selection:bg-brand selection:text-on-brand file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium file:text-foreground",
                fieldClasses,
                className
            )}
            {...props}
        />
    )
}

export { Input, fieldClasses }
