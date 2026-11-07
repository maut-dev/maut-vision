import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

/** The root of a bounded section: it fills the main area, never scrolls itself and lets its Panes scroll. */
export function SectionLayout({ className, ...props }: ComponentProps<"div">) {
    return <div className={cn("flex min-h-0 flex-1 flex-col gap-3 overflow-hidden", className)} {...props} />;
}
