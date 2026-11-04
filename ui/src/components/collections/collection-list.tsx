import type { ComponentProps, FormEvent, ReactNode } from "react";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";

/**
 * The "new set / new group" form above a master-detail section: one name field and the create button. Texts and test ids come from
 * the section.
 */
export function CollectionCreateForm({ label, inputLabel = label, placeholder, submitText, value, setValue, busy, submit, inputTestId, submitTestId }: {
    label: string;
    /** The accessible name of the field when it differs from the visible label. */
    inputLabel?: string;
    placeholder: string;
    submitText: string;
    value: string;
    setValue: (value: string) => void;
    busy: boolean;
    submit: (name: string) => void;
    inputTestId: string;
    submitTestId: string;
}) {
    function onSubmit(event: FormEvent) {
        event.preventDefault();
        const name = value.trim();
        if (name && !busy) submit(name);
    }

    return (
        <form className="flex max-w-3xl shrink-0 items-center gap-3" onSubmit={onSubmit}>
            <label className="flex min-w-0 flex-1 items-center gap-3">
                <span className="hidden shrink-0 text-xs text-text-2 md:inline">{label}</span>
                <Input required maxLength={120} aria-label={inputLabel} placeholder={placeholder} data-testid={inputTestId}
                    value={value} onChange={event => setValue(event.target.value)} />
            </label>
            <Button type="submit" data-testid={submitTestId} disabled={busy || !value.trim()}>{submitText}</Button>
        </form>
    );
}

/**
 * The list column of a master-detail section: one scroll, the loading line, an error with "Retry" and the empty hint. The items are
 * CollectionItem buttons.
 */
export function CollectionList({ label, testId, loading, loadingText, error, retry, retryText, empty, children }: {
    label: string;
    testId: string;
    /** The first read is running and nothing is listed yet. */
    loading: boolean;
    loadingText: string;
    error?: string;
    retry: () => void;
    retryText: string;
    /** Shown when the list is read and has no items. */
    empty?: ReactNode;
    children: ReactNode;
}) {
    return (
        <Pane surface role="region" aria-label={label} data-testid={testId} tabIndex={0} className="p-2 outline-none focus-visible:ring-[3px] focus-visible:ring-focus/30">
            <div className="space-y-1">
                {children}
                {loading && <p role="status" className="p-2 text-sm text-text-2">{loadingText}</p>}
                {error && <Notice variant="destructive" action={{ label: retryText, onClick: retry }}>{error}</Notice>}
                {empty}
            </div>
        </Pane>
    );
}

/** One set or group of the list: a toggle button with the name, a summary line and marks; extra data-* attributes go to the button. */
export function CollectionItem({ selected, title, summary, children, className, ...props }: Omit<ComponentProps<"button">, "title"> & {
    selected: boolean;
    title: ReactNode;
    summary: ReactNode;
}) {
    return (
        <button
            type="button"
            aria-pressed={selected}
            className={cn(
                "w-full rounded-md border px-3 py-2.5 text-left text-text-1 outline-none transition-colors duration-120 ease-glass focus-visible:ring-[3px] focus-visible:ring-focus/30",
                selected ? "border-line bg-glass-fill-hover shadow-[var(--glass-highlight)]" : "border-transparent hover:bg-glass-fill",
                className
            )}
            {...props}
        >
            <span className="block text-sm font-medium break-words">{title}</span>
            {summary}
            {children}
        </button>
    );
}
