import { useCallback, useEffect, useRef, useState } from "react";

type FocusTarget = () => HTMLElement | null | undefined;

/**
 * Moves the focus after React commits the next render in which the section is not busy. A control disabled while an action runs
 * (busy) can take the focus only after the render that enables it; a frame (requestAnimationFrame) may come before that render, and
 * focus() on a disabled control does nothing. The request itself causes a render, so it never waits for an unrelated update.
 */
export function useFocusAfterRender(busy: boolean): (target: FocusTarget) => void {
    const pending = useRef<FocusTarget>(undefined);
    const [, setRequest] = useState(0);

    useEffect(() => {
        if (busy || !pending.current) return;
        const target = pending.current();
        pending.current = undefined;
        target?.focus();
    });

    return useCallback((target: FocusTarget) => {
        pending.current = target;
        setRequest(count => count + 1);
    }, []);
}
