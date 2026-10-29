import { useCallback, useEffect, useRef, useState } from "react";

/** The selection limit: operations on the selection take up to 400 items per call. */
export const selectionLimit = 400;

export interface SelectionMessages {
    /** An attempt to select more than the limit. */
    limit: string;
    /** Select all on the page when more than the limit is loaded. */
    firstPage: string;
}

/**
 * The selection of gallery tiles: a click toggles, Shift+click adds the range from the last marked tile,
 * Select all on the page takes the loaded ones (up to the limit) or clears the selection. A change of resetKey (group,
 * set, filter) clears the selection.
 */
export function useRangeSelection<T extends { id: string }>(items: readonly T[], resetKey: string, messages: SelectionMessages, notify: (text: string) => void) {
    const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
    const anchor = useRef<number | undefined>(undefined);

    const clear = useCallback(() => {
        setSelected(new Set());
        anchor.current = undefined;
    }, []);

    useEffect(clear, [resetKey, clear]);

    function toggle(item: T, index: number, range: boolean) {
        setSelected(current => {
            const next = new Set(current);
            if (range && anchor.current !== undefined) {
                const from = Math.min(anchor.current, index), to = Math.max(anchor.current, index);
                for (let position = from; position <= to && next.size < selectionLimit; position++) next.add(items[position].id);
                if (to - from + 1 + current.size > selectionLimit) notify(messages.limit);
            } else if (next.has(item.id)) next.delete(item.id);
            else if (next.size < selectionLimit) next.add(item.id);
            else notify(messages.limit);
            return next;
        });
        anchor.current = index;
    }

    function selectPage() {
        if (selected.size) {
            clear();
            return;
        }
        setSelected(new Set(items.slice(0, selectionLimit).map(item => item.id)));
        if (items.length > selectionLimit) notify(messages.firstPage);
    }

    /** Removes items that disappeared from the selection (deleted, filtered out). */
    function forget(ids: readonly string[]) {
        setSelected(current => {
            if (!ids.some(id => current.has(id))) return current;
            const next = new Set(current);
            for (const id of ids) next.delete(id);
            return next;
        });
    }

    return { selected, toggle, selectPage, clear, forget };
}
