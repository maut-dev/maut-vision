import { useId, type ComponentProps, type ReactNode } from "react";
import { cn } from "@/lib/utils";

interface PanelProps extends Omit<ComponentProps<"section">, "title"> {
    title?: ReactNode;
    description?: ReactNode;
    actions?: ReactNode;
}

/** A solid content card; with a title it is a labelled region with the title, the description and actions in its header. */
export function Panel({ title, description, actions, className, children, ...props }: PanelProps) {
    const titleId = useId();
    const header = title !== undefined || description !== undefined || actions !== undefined;
    return (
        <section aria-labelledby={title !== undefined ? titleId : undefined} className={cn("surface grid gap-4 p-5", className)} {...props}>
            {header && (
                <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0 space-y-1">
                        {title !== undefined && <h2 id={titleId} className="text-[15px] leading-tight font-semibold tracking-[-0.015em] break-words">{title}</h2>}
                        {description !== undefined && <p className="text-[13px] text-text-2">{description}</p>}
                    </div>
                    {actions !== undefined && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
                </div>
            )}
            {children}
        </section>
    );
}
