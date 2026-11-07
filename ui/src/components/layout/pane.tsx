import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

/**
 * A region with exactly one scroll; surface gives it the solid content background and a line. It is positioned, so absolutely positioned
 * descendants (the hidden native inputs of Radix Select and Checkbox) are clipped by it instead of stretching the document.
 */
export function Pane({ surface = false, className, ...props }: ComponentProps<"div"> & { surface?: boolean }) {
    return <div className={cn("relative min-h-0 overflow-y-auto overscroll-contain", surface && "surface", className)} {...props} />;
}
