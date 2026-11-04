import type { ComponentProps } from "react";
import { cn } from "@/lib/utils";

/** A list column and a detail column that fill the rest of a bounded section; the two children are the columns. */
export function MasterDetail({ className, ...props }: ComponentProps<"div">) {
    return <div className={cn("grid min-h-0 flex-1 grid-cols-[150px_minmax(0,1fr)] gap-5 lg:grid-cols-[220px_minmax(0,1fr)]", className)} {...props} />;
}
