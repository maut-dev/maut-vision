import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "@/lib/utils"
import { Slot } from "radix-ui"

const buttonVariants = cva(
    "inline-flex shrink-0 items-center justify-center gap-2 rounded-md border border-transparent text-[13px] font-medium whitespace-nowrap transition-[background-color,border-color,color,box-shadow,transform] duration-120 ease-glass outline-none active:translate-y-px focus-visible:border-focus focus-visible:ring-[3px] focus-visible:ring-focus/30 disabled:pointer-events-none disabled:opacity-45 aria-invalid:border-danger [&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4",
    {
        variants: {
            variant: {
                // The main action inside a form or a dialog: white on ink.
                default: "bg-text-1 text-ink-0 hover:bg-text-2",
                // The one primary action of a view: lime is rare (docs/UI/design-system.md).
                brand: "bg-brand font-semibold text-on-brand hover:bg-brand-strong",
                destructive: "border-danger/35 bg-transparent text-danger hover:bg-danger/12",
                outline: "border-glass-edge bg-glass-fill text-text-1 shadow-highlight hover:border-line-strong hover:bg-glass-fill-hover",
                secondary: "border-glass-edge bg-glass-fill text-text-1 shadow-highlight hover:border-line-strong hover:bg-glass-fill-hover",
                glass: "border-glass-edge bg-glass-fill text-text-1 shadow-highlight hover:border-line-strong hover:bg-glass-fill-hover",
                ghost: "bg-transparent text-text-1 hover:bg-glass-fill",
                link: "border-0 text-primary underline-offset-4 hover:underline active:translate-y-0",
            },
            size: {
                default: "h-9 px-4 py-2 has-[>svg]:px-3",
                xs: "h-6 gap-1 px-2 text-xs has-[>svg]:px-1.5 [&_svg:not([class*='size-'])]:size-3",
                sm: "h-8 gap-1.5 px-3 has-[>svg]:px-2.5",
                lg: "h-10 rounded-lg px-6 text-sm has-[>svg]:px-4",
                icon: "size-9",
                "icon-xs": "size-6 [&_svg:not([class*='size-'])]:size-3",
                "icon-sm": "size-8",
                "icon-lg": "size-10 rounded-lg",
            },
        },
        defaultVariants: {
            variant: "default",
            size: "default",
        },
    }
)

function Button({
    className,
    variant = "default",
    size = "default",
    asChild = false,
    ...props
}: React.ComponentProps<"button"> &
    VariantProps<typeof buttonVariants> & {
        asChild?: boolean
    }) {
    const Comp = asChild ? Slot.Root : "button"

    return (
        <Comp
            data-slot="button"
            data-variant={variant}
            data-size={size}
            className={cn(buttonVariants({ variant, size, className }))}
            {...props}
        />
    )
}

export { Button, buttonVariants }
