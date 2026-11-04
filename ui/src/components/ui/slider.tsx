import * as React from "react"
import { Slider as SliderPrimitive } from "radix-ui"
import { cn } from "@/lib/utils"

function Slider({
    className,
    defaultValue,
    value,
    min = 0,
    max = 100,
    thumbLabels,
    ...props
}: React.ComponentProps<typeof SliderPrimitive.Root> & { thumbLabels?: readonly string[] }) {
    const values = React.useMemo(
        () => Array.isArray(value) ? value : Array.isArray(defaultValue) ? defaultValue : [min, max],
        [value, defaultValue, min, max]
    )

    return (
        <SliderPrimitive.Root
            data-slot="slider"
            defaultValue={defaultValue}
            value={value}
            min={min}
            max={max}
            className={cn(
                "relative flex w-full touch-none items-center select-none data-[disabled]:opacity-50 data-[orientation=vertical]:h-full data-[orientation=vertical]:min-h-44 data-[orientation=vertical]:w-auto data-[orientation=vertical]:flex-col",
                className
            )}
            {...props}
        >
            <SliderPrimitive.Track
                data-slot="slider-track"
                className="relative grow overflow-hidden rounded-full bg-ink-3 data-[orientation=horizontal]:h-1 data-[orientation=horizontal]:w-full data-[orientation=vertical]:h-full data-[orientation=vertical]:w-1"
            >
                <SliderPrimitive.Range
                    data-slot="slider-range"
                    className="absolute bg-brand data-[orientation=horizontal]:h-full data-[orientation=vertical]:w-full"
                />
            </SliderPrimitive.Track>
            {values.map((_, index) => (
                <SliderPrimitive.Thumb
                    data-slot="slider-thumb"
                    key={index}
                    aria-label={thumbLabels?.[index]}
                    className="block size-4 shrink-0 rounded-full border-[3px] border-ink-0 bg-text-1 shadow-[0_0_0_1px_var(--line-strong),var(--shadow-1)] transition-[box-shadow] duration-120 ease-glass hover:shadow-[0_0_0_1px_var(--line-strong),0_0_0_4px_color-mix(in_srgb,var(--focus)_20%,transparent)] focus-visible:shadow-[0_0_0_1px_var(--focus),0_0_0_4px_color-mix(in_srgb,var(--focus)_30%,transparent)] focus-visible:outline-hidden disabled:pointer-events-none disabled:opacity-50"
                />
            ))}
        </SliderPrimitive.Root>
    )
}

export { Slider }
