import * as React from "react"
import { cva, type VariantProps } from "class-variance-authority"
import { cn } from "@/lib/utils"
import { Slot } from "radix-ui"

// A 22 px pill with a status dot. It never widens its parent: long text wraps inside max-w-full.
const badgeVariants = cva(
    "inline-flex min-h-[22px] w-fit max-w-full shrink-0 items-center gap-1.5 rounded-full border border-line bg-glass-fill px-2 py-[3px] text-left text-[11px] leading-[14px] font-medium tracking-[0.01em] text-text-2 [overflow-wrap:anywhere] transition-[color,box-shadow] outline-none before:size-1.5 before:shrink-0 before:rounded-full before:bg-text-3 has-[>svg]:before:hidden focus-visible:border-focus focus-visible:ring-[3px] focus-visible:ring-focus/30 [&>svg]:pointer-events-none [&>svg]:size-3 [&>svg]:shrink-0",
    {
        variants: {
            variant: {
                default: "border-transparent bg-text-1 text-ink-0 before:bg-ink-0 [a&]:hover:bg-text-2",
                secondary: "[a&]:hover:bg-glass-fill-hover",
                destructive: "border-danger/35 bg-danger/10 text-danger before:bg-danger [a&]:hover:bg-danger/15",
                outline: "border-line-strong bg-transparent [a&]:hover:bg-glass-fill",
                ghost: "border-transparent bg-transparent [a&]:hover:bg-glass-fill",
                link: "border-transparent bg-transparent text-primary underline-offset-4 before:hidden [a&]:hover:underline",
                ok: "before:bg-ok",
                warn: "before:bg-warn",
                danger: "before:bg-danger",
                info: "before:bg-info",
                brand: "border-transparent bg-brand-soft text-brand before:bg-brand before:shadow-[0_0_8px_var(--brand)]",
                plain: "before:hidden",
            },
        },
        defaultVariants: {
            variant: "default",
        },
    }
)

function Badge({
    className,
    variant = "default",
    asChild = false,
    ...props
}: React.ComponentProps<"span"> &
    VariantProps<typeof badgeVariants> & { asChild?: boolean }) {
    const Comp = asChild ? Slot.Root : "span"

    return (
        <Comp
            data-slot="badge"
            data-variant={variant}
            className={cn(badgeVariants({ variant }), className)}
            {...props}
        />
    )
}

export { Badge, badgeVariants }
