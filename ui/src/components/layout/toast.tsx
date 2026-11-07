import type { ComponentProps, ReactNode } from "react";
import { CircleAlert, CircleCheck, Info, TriangleAlert, X, type LucideIcon } from "lucide-react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

type ToastVariant = "danger" | "warn" | "ok" | "info";

const icons: Record<ToastVariant, { icon: LucideIcon; color: string }> = {
    danger: { icon: CircleAlert, color: "text-danger" },
    warn: { icon: TriangleAlert, color: "text-warn" },
    ok: { icon: CircleCheck, color: "text-ok" },
    info: { icon: Info, color: "text-info" },
};

interface ToastProps extends Omit<ComponentProps<"div">, "title"> {
    variant?: ToastVariant;
    /** The status word, for example «Ошибка»: the state is never told by the colour alone. */
    title: ReactNode;
    /** The accessible name of the close button. */
    closeLabel: string;
    onClose: () => void;
}

/**
 * A message that floats over the window (raised glass): an icon and a status word, the text that wraps and a close button. An error is
 * announced at once, anything else politely; the owner places it in a live region and removes it on close.
 */
export function Toast({ variant = "danger", title, closeLabel, onClose, className, children, ...props }: ToastProps) {
    const { icon: Icon, color } = icons[variant];
    return (
        <div role={variant === "danger" ? "alert" : "status"} className={cn("glass-raised flex items-start gap-3 rounded-lg py-3 pr-2 pl-3 text-[13px] text-text-1", className)} {...props}>
            <Icon className={cn("mt-0.5 size-4 shrink-0", color)} aria-hidden />
            <div className="min-w-0 flex-1 space-y-0.5">
                <p className="font-medium">{title}</p>
                <div className="text-text-2 [overflow-wrap:anywhere]">{children}</div>
            </div>
            <Button type="button" size="icon-xs" variant="ghost" aria-label={closeLabel} title={closeLabel} onClick={onClose}>
                <X />
            </Button>
        </div>
    );
}
