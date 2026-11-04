import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

/** A row of actions and filters that wraps instead of overflowing the window. */
export function Toolbar({ className, ...props }: ComponentProps<"div">) {
    return <div className={cn("flex flex-wrap items-center gap-2", className)} {...props} />;
}
