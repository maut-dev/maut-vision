import type { ComponentProps, ReactNode } from "react";
import type { LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";

interface EmptyStateProps extends Omit<ComponentProps<"div">, "title"> {
    title: ReactNode;
    description?: ReactNode;
    icon?: LucideIcon;
    action?: ReactNode;
}

/** What a list or a region shows when it has nothing yet: a quiet icon, a title, a hint and the action that fills it. */
export function EmptyState({ title, description, icon: Icon, action, className, ...props }: EmptyStateProps) {
    return (
        <div className={cn("flex flex-col items-center justify-center gap-2 px-4 py-8 text-center", className)} {...props}>
            {Icon && <Icon className="mb-1 size-6 text-text-3" aria-hidden />}
            <p className="text-sm font-medium text-text-1">{title}</p>
            {description !== undefined && <p className="max-w-prose text-[13px] text-text-2">{description}</p>}
            {action !== undefined && <div className="mt-2 flex flex-wrap justify-center gap-2">{action}</div>}
        </div>
    );
}
