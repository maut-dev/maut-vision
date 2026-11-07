import type { ComponentProps } from "react";
import { Tabs as TabsPrimitive } from "radix-ui";
import { formatNumber } from "@/lib/format";
import { cn } from "@/lib/utils";

/** Tabs of a section (Radix): arrows move between tabs; the content of a hidden tab unmounts unless it has forceMount. */
export function Tabs({ className, ...props }: ComponentProps<typeof TabsPrimitive.Root>) {
    return <TabsPrimitive.Root className={cn("flex min-h-0 flex-col gap-3", className)} {...props} />;
}

/** The segmented control that holds the tabs; it wraps in a narrow window. */
export function TabsList({ className, ...props }: ComponentProps<typeof TabsPrimitive.List>) {
    return (
        <TabsPrimitive.List
            className={cn("inline-flex w-fit max-w-full flex-wrap gap-0.5 rounded-lg border border-line bg-ink-2 p-[3px]", className)}
            {...props}
        />
    );
}

interface TabsTriggerProps extends ComponentProps<typeof TabsPrimitive.Trigger> {
    /** A count after the label, for example the number of runs. */
    count?: number;
    /** Something runs in this tab right now. */
    live?: boolean;
}

/** One tab: the label, an optional count and a live dot. */
export function TabsTrigger({ count, live = false, className, children, ...props }: TabsTriggerProps) {
    return (
        <TabsPrimitive.Trigger
            className={cn(
                "inline-flex h-7 items-center gap-1.5 rounded-md px-4 text-[13px] font-medium whitespace-nowrap text-text-2 transition-[background-color,color,box-shadow] duration-120 ease-glass outline-none hover:text-text-1 focus-visible:ring-[3px] focus-visible:ring-focus/30 disabled:pointer-events-none disabled:opacity-45 data-[state=active]:bg-glass-fill-hover data-[state=active]:text-text-1 data-[state=active]:shadow-[var(--glass-highlight),var(--shadow-1)]",
                className
            )}
            {...props}
        >
            {children}
            {count !== undefined && <span className="text-text-3 tabular-nums">{formatNumber(count)}</span>}
            {live && <span className="size-[7px] shrink-0 rounded-full bg-brand shadow-[0_0_10px_var(--brand)]" aria-hidden />}
        </TabsPrimitive.Trigger>
    );
}

/** The content of a tab; it takes the rest of the height so that a Pane inside it can scroll. */
export function TabsContent({ className, ...props }: ComponentProps<typeof TabsPrimitive.Content>) {
    return <TabsPrimitive.Content className={cn("min-h-0 flex-1 outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30", className)} {...props} />;
}
