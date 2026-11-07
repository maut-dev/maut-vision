import { useId, useState, type ComponentProps, type ReactNode } from "react";
import { ChevronRight } from "lucide-react";
import { cn } from "@/lib/utils";

interface CollapsibleProps extends Omit<ComponentProps<"section">, "title"> {
    title: ReactNode;
    description?: ReactNode;
    /** Controlled state; without it the disclosure keeps its own, starting from defaultOpen. */
    open?: boolean;
    defaultOpen?: boolean;
    onOpenChange?: (open: boolean) => void;
    /** The closed content stays mounted (hidden), so its state and requests survive closing. */
    keepMounted?: boolean;
    "data-testid"?: string;
}

/** A disclosure: a button with aria-expanded and a chevron shows the content; data-testid "x" names the button "x-toggle". */
export function Collapsible({ title, description, open, defaultOpen = false, onOpenChange, keepMounted = false, className, children, ...props }: CollapsibleProps) {
    const [own, setOwn] = useState(defaultOpen);
    const expanded = open ?? own;
    const bodyId = useId();
    const testId = props["data-testid"];

    function toggle() {
        if (open === undefined) setOwn(!expanded);
        onOpenChange?.(!expanded);
    }

    return (
        <section className={cn("min-w-0", className)} {...props}>
            <button
                type="button"
                aria-expanded={expanded}
                aria-controls={expanded || keepMounted ? bodyId : undefined}
                data-testid={testId ? `${testId}-toggle` : undefined}
                className="flex w-full items-start gap-2 rounded-md px-3 py-2 text-left outline-none transition-colors duration-120 ease-glass hover:bg-glass-fill focus-visible:ring-[3px] focus-visible:ring-focus/30"
                onClick={toggle}
            >
                <ChevronRight className={cn("mt-0.5 size-4 shrink-0 text-text-2 transition-transform duration-220 ease-glass", expanded && "rotate-90")} aria-hidden />
                <span className="min-w-0">
                    <span className="block text-sm font-medium">{title}</span>
                    {description !== undefined && <span className="block text-xs text-text-2">{description}</span>}
                </span>
            </button>
            {(expanded || keepMounted) && (
                <div id={bodyId} hidden={!expanded} className="min-w-0">
                    {children}
                </div>
            )}
        </section>
    );
}
