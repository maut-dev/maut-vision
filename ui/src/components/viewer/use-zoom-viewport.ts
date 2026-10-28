import { useCallback, useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from "react";
import { fitScale, frameOffset, scaleOf, scrollForCenter, viewCenter, zoomIn, zoomOut, type Size, type ZoomMode } from "@/lib/annotation/zoom";

/** Нажатие клавиши: и событие React, и событие окна. */
export interface KeyInput {
    key: string;
    target: EventTarget | null;
    ctrlKey: boolean;
    altKey: boolean;
    metaKey: boolean;
    preventDefault(): void;
}

/** Поля ввода сами обрабатывают клавиши: масштаб и переходы не перехватывают их. */
export function isTypingTarget(target: EventTarget | null): boolean {
    return target instanceof HTMLElement && !!target.closest("input,select,textarea,[contenteditable='true']");
}

/**
 * Область просмотра кадра с масштабом и перемещением: общий механизм просмотрщика и редактора разметки.
 * ref подключается к прокручиваемому элементу; холст (canvas) не меньше области, кадр (frame) стоит в нём по центру
 * с отступом offset. Ctrl+колесо, клавиши +/=, -, 0 и кнопки меняют масштаб, сохраняя центр; перетаскивание
 * (beginPan/movePan/endPan) прокручивает кадр. resetKey (новый кадр) возвращает «Вписать».
 */
export function useZoomViewport(image: Size, resetKey: unknown) {
    const [element, setElement] = useState<HTMLDivElement | null>(null);
    const [viewport, setViewport] = useState<Size>({ width: 1, height: 1 });
    const [mode, setMode] = useState<ZoomMode>("fit");
    const pendingCenter = useRef<{ x: number; y: number } | null>(null);
    const pan = useRef<{ x: number; y: number; left: number; top: number } | undefined>(undefined);
    const scale = scaleOf(mode, image, viewport);
    const fit = fitScale(image, viewport);
    const frame = { width: image.width * scale, height: image.height * scale };
    const canvas = { width: Math.max(viewport.width, frame.width), height: Math.max(viewport.height, frame.height) };
    const offset = { left: frameOffset(viewport.width, frame.width), top: frameOffset(viewport.height, frame.height) };

    useEffect(() => {
        if (!element) return;
        const measure = () => setViewport({ width: Math.max(1, element.clientWidth), height: Math.max(1, element.clientHeight) });
        measure();
        const observer = new ResizeObserver(measure);
        observer.observe(element);
        return () => observer.disconnect();
    }, [element]);

    useEffect(() => {
        setMode("fit");
        pendingCenter.current = null;
        element?.scrollTo({ left: 0, top: 0 });
    }, [resetKey, element]);

    useLayoutEffect(() => {
        const center = pendingCenter.current;
        if (!element || !center) return;
        pendingCenter.current = null;
        const target = scrollForCenter(center, viewport, image, scale);
        element.scrollLeft = target.left;
        element.scrollTop = target.top;
    });

    const latest = useRef({ element, viewport, image, scale });
    latest.current = { element, viewport, image, scale };

    const zoom = useCallback((next: ZoomMode) => {
        const current = latest.current;
        if (current.element) {
            pendingCenter.current = viewCenter({ left: current.element.scrollLeft, top: current.element.scrollTop }, current.viewport, current.image, current.scale);
        }
        setMode(next);
    }, []);

    const reset = useCallback(() => {
        pendingCenter.current = null;
        setMode("fit");
        latest.current.element?.scrollTo({ left: 0, top: 0 });
    }, []);

    useEffect(() => {
        if (!element) return;
        const wheel = (event: WheelEvent) => {
            if (!event.ctrlKey) return;
            event.preventDefault();
            const current = latest.current.scale;
            zoom(event.deltaY < 0 ? zoomIn(current) : zoomOut(current));
        };
        element.addEventListener("wheel", wheel, { passive: false });
        return () => element.removeEventListener("wheel", wheel);
    }, [element, zoom]);

    /** +/=, -, 0; true — клавиша обработана. */
    function handleZoomKey(event: KeyInput): boolean {
        if (event.ctrlKey || event.altKey || event.metaKey || isTypingTarget(event.target)) return false;
        if (event.key === "+" || event.key === "=") zoom(zoomIn(scale));
        else if (event.key === "-") zoom(zoomOut(scale));
        else if (event.key === "0") reset();
        else return false;
        event.preventDefault();
        return true;
    }

    function beginPan(event: PointerEvent<HTMLElement>) {
        if (!element) return;
        try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* synthetic pointers cannot be captured */ }
        pan.current = { x: event.clientX, y: event.clientY, left: element.scrollLeft, top: element.scrollTop };
    }

    /** true — идёт перемещение, событие обработано. */
    function movePan(event: PointerEvent<HTMLElement>): boolean {
        const start = pan.current;
        if (!start || !element) return false;
        element.scrollLeft = start.left - (event.clientX - start.x);
        element.scrollTop = start.top - (event.clientY - start.y);
        return true;
    }

    function endPan(): boolean {
        const panning = !!pan.current;
        pan.current = undefined;
        return panning;
    }

    return {
        ref: setElement,
        mode,
        scale,
        fit,
        frame,
        canvas,
        offset,
        zoom,
        zoomIn: () => zoom(zoomIn(scale)),
        zoomOut: () => zoom(zoomOut(scale)),
        reset,
        handleZoomKey,
        beginPan,
        movePan,
        endPan,
        get panning() { return !!pan.current; },
    };
}

export type ZoomViewport = ReturnType<typeof useZoomViewport>;
