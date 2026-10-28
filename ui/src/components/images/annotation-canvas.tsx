import { useEffect, useRef, useState, type PointerEvent } from "react";
import type { ImageResponseDto } from "@/api/projects/images/response-dto/image-response-dto";
import { ResourceImage } from "@/components/resource-image";
import type { ZoomViewport } from "@/components/viewer/use-zoom-viewport";
import { isTypingTarget } from "@/components/viewer/use-zoom-viewport";
import { addObject, newObjectPrefix, updateObject, type AnnotationDraft } from "@/lib/annotation/draft";
import {
    boxFromPoints,
    handlePosition,
    handles,
    isUsableBox,
    moveBox,
    pointInFrame,
    resizeBox,
    sameBox,
    type Box,
    type Handle,
    type Point,
} from "@/lib/annotation/geometry";
import { contrastText, roiColor } from "@/lib/variant-color";
import type { AnnotationPalette } from "./annotation-palette";

export type AnnotationTool = "object" | "roi";

/** Выделенная рамка: ID объекта или "roi". */
export type Selection = string | null;

type Gesture =
    | { kind: "draw"; start: Point }
    | { kind: "move"; id: string; start: Point; origin: Box }
    | { kind: "resize"; id: string; handle: Handle; origin: Box }
    | { kind: "pan" };

const handleCursor: Record<Handle, string> = {
    n: "ns-resize", s: "ns-resize", e: "ew-resize", w: "ew-resize", ne: "nesw-resize", sw: "nesw-resize", nw: "nwse-resize", se: "nwse-resize",
};

/**
 * Холст редактора: кадр в области масштаба и перемещения, рамки объектов и ROI, маркеры выделенной рамки.
 * Протяжка по свободному месту рисует рамку текущим инструментом; по рамке — выделяет и перемещает её, Shift —
 * всегда рисует новую. Пробел или средняя кнопка с перетаскиванием перемещают кадр. Координаты указателя считаются
 * от экранного прямоугольника кадра, поэтому верны при любом масштабе и прокрутке. Изменение попадает в историю
 * одним шагом по отпусканию кнопки.
 */
export function AnnotationCanvas({ image, draft, selection, tool, classId, classVariantId, palette, locked, zoom, select, commit, renew }: {
    image: ImageResponseDto;
    draft: AnnotationDraft | undefined;
    selection: Selection;
    tool: AnnotationTool;
    classId: string;
    classVariantId: string | null;
    palette: AnnotationPalette;
    locked: boolean;
    zoom: ZoomViewport;
    select: (selection: Selection) => void;
    commit: (next: AnnotationDraft, selection: Selection) => void;
    renew: () => void;
}) {
    const frame = useRef<HTMLDivElement>(null);
    const gesture = useRef<Gesture | undefined>(undefined);
    const [preview, setPreview] = useState<{ id: string; box: Box } | { id: "draw"; box: Box }>();
    const [space, setSpace] = useState(false);

    useEffect(() => {
        const down = (event: KeyboardEvent) => { if (event.key === " " && !isTypingTarget(event.target) && !event.repeat) setSpace(true); };
        const up = (event: KeyboardEvent) => { if (event.key === " ") setSpace(false); };
        const blur = () => setSpace(false);
        window.addEventListener("keydown", down);
        window.addEventListener("keyup", up);
        window.addEventListener("blur", blur);
        return () => {
            window.removeEventListener("keydown", down);
            window.removeEventListener("keyup", up);
            window.removeEventListener("blur", blur);
        };
    }, []);

    const point = (event: PointerEvent) => pointInFrame(event.clientX, event.clientY, frame.current!.getBoundingClientRect());
    const boxOf = (id: string): Box | null => id === "roi" ? draft?.roi ?? null : draft?.objects.find(item => item.id === id)?.box ?? null;

    function capture(event: PointerEvent<HTMLElement>) {
        try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* synthetic pointers cannot be captured */ }
    }

    function onPointerDown(event: PointerEvent<HTMLDivElement>) {
        if (event.button === 1 || (event.button === 0 && space)) {
            event.preventDefault();
            gesture.current = { kind: "pan" };
            zoom.beginPan(event);
            return;
        }
        if (event.button !== 0 || !draft || !frame.current) return;
        if (locked) {
            gesture.current = { kind: "pan" };
            zoom.beginPan(event);
            return;
        }
        const target = event.target as HTMLElement;
        const handle = target.closest<HTMLElement>("[data-handle]");
        const box = target.closest<HTMLElement>("[data-box-id]");
        if (handle && selection) {
            const origin = boxOf(selection);
            if (origin) {
                capture(event);
                gesture.current = { kind: "resize", id: selection, handle: handle.dataset.handle as Handle, origin };
                return;
            }
        }
        if (box && !event.shiftKey) {
            const id = box.dataset.boxId!;
            const origin = boxOf(id);
            if (origin) {
                capture(event);
                select(id);
                gesture.current = { kind: "move", id, start: point(event), origin };
                return;
            }
        }
        const rect = frame.current.getBoundingClientRect();
        if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) {
            select(null);
            return;
        }
        if (tool === "object" && !classId) return;
        capture(event);
        select(null);
        gesture.current = { kind: "draw", start: point(event) };
    }

    function onPointerMove(event: PointerEvent<HTMLDivElement>) {
        const current = gesture.current;
        if (!current) return;
        if (current.kind === "pan") {
            zoom.movePan(event);
            return;
        }
        const at = point(event);
        if (current.kind === "draw") setPreview({ id: "draw", box: boxFromPoints(current.start, at) });
        else if (current.kind === "move") setPreview({ id: current.id, box: moveBox(current.origin, at.x - current.start.x, at.y - current.start.y) });
        else setPreview({ id: current.id, box: resizeBox(current.origin, current.handle, at) });
    }

    function finish(event: PointerEvent<HTMLDivElement> | null) {
        const current = gesture.current;
        gesture.current = undefined;
        setPreview(undefined);
        if (!current) return;
        if (current.kind === "pan") {
            zoom.endPan();
            return;
        }
        if (!event || !draft) return;
        const at = point(event);
        if (current.kind === "draw") {
            const box = boxFromPoints(current.start, at);
            if (!isUsableBox(box)) return;
            if (tool === "roi") commit({ ...draft, roi: box }, "roi");
            else {
                const id = newObjectPrefix + crypto.randomUUID();
                commit(addObject(draft, classId, classVariantId, box, id), id);
            }
            return;
        }
        const box = current.kind === "move"
            ? moveBox(current.origin, at.x - current.start.x, at.y - current.start.y)
            : resizeBox(current.origin, current.handle, at);
        if (sameBox(box, current.origin)) return;
        if (current.id === "roi") commit({ ...draft, roi: box }, "roi");
        else commit(updateObject(draft, current.id, { box }), current.id);
    }

    const shown = (id: string, box: Box) => preview && preview.id === id ? preview.box : box;
    const drawColor = tool === "roi" ? roiColor : palette.colorOf(classId, classVariantId);
    const selectedBox = selection ? (() => { const box = boxOf(selection); return box ? shown(selection, box) : null; })() : null;
    const cursor = space || gesture.current?.kind === "pan" ? "grab" : locked ? "default" : "crosshair";

    return (
        <div
            ref={zoom.ref}
            data-annotation-viewport
            role="region"
            aria-label="Кадр для разметки: протяжка рисует рамку, пробел с перетаскиванием перемещает кадр"
            tabIndex={0}
            className="relative h-[min(68dvh,720px)] min-h-72 overflow-auto overscroll-contain rounded-md border bg-muted/30 outline-none select-none focus-visible:ring-2 focus-visible:ring-ring"
            style={{ touchAction: "none", cursor }}
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={event => finish(event)}
            onPointerCancel={() => finish(null)}
            onAuxClick={event => event.preventDefault()}
        >
            <div style={{ width: zoom.canvas.width, height: zoom.canvas.height, position: "relative" }}>
                <div
                    ref={frame}
                    data-annotation-canvas
                    style={{ position: "absolute", left: zoom.offset.left, top: zoom.offset.top, width: zoom.frame.width, height: zoom.frame.height }}
                >
                    <ResourceImage key={image.id} src={image.originalUrl} alt={image.displayName} fit="contain" className="size-full rounded-none" onExpired={renew} />
                    {draft?.roi && (
                        <BoxFrame
                            id="roi"
                            box={shown("roi", draft.roi)}
                            color={roiColor}
                            label="Область ROI"
                            dashed
                            selected={selection === "roi"}
                            interactive={!locked && tool === "roi"}
                        />
                    )}
                    {draft?.objects.map((item, index) => (
                        <BoxFrame
                            key={item.id}
                            id={item.id}
                            box={shown(item.id, item.box)}
                            color={palette.colorOf(item.classId, item.classVariantId)}
                            label={`${index + 1}. ${palette.labelOf(item.classId, item.classVariantId)}`}
                            selected={selection === item.id}
                            interactive={!locked && tool === "object"}
                        />
                    ))}
                    {preview?.id === "draw" && (
                        <div className="pointer-events-none absolute border-2 border-dashed" data-testid="draw-preview" style={{ ...percent(preview.box), borderColor: drawColor }} />
                    )}
                    {selectedBox && !locked && handles.map(handle => {
                        const at = handlePosition(selectedBox, handle);
                        return (
                            <span
                                key={handle}
                                data-handle={handle}
                                className="absolute size-2.5 -translate-x-1/2 -translate-y-1/2 rounded-[2px] border border-black/60 bg-white"
                                style={{ left: `${at.x * 100}%`, top: `${at.y * 100}%`, cursor: handleCursor[handle] }}
                            />
                        );
                    })}
                </div>
            </div>
        </div>
    );
}

const percent = (box: Box) => ({ left: `${box.x * 100}%`, top: `${box.y * 100}%`, width: `${box.width * 100}%`, height: `${box.height * 100}%` });

function BoxFrame({ id, box, color, label, dashed = false, selected, interactive }: {
    id: string;
    box: Box;
    color: string;
    label: string;
    dashed?: boolean;
    selected: boolean;
    interactive: boolean;
}) {
    return (
        <div
            data-box-id={id}
            data-selected={selected || undefined}
            className={`absolute ${dashed ? "border-2 border-dashed" : selected ? "border-[3px]" : "border-2"} ${interactive ? "cursor-move" : "pointer-events-none"}`}
            style={{ ...percent(box), borderColor: color, backgroundColor: dashed ? undefined : `${color}${selected ? "33" : "1f"}` }}
        >
            <span
                className={`pointer-events-none absolute left-[-2px] px-1 text-[10px] leading-4 font-medium whitespace-nowrap ${box.y < 0.05 ? "top-0" : "bottom-full"}`}
                style={{ backgroundColor: color, color: contrastText(color) }}
            >
                {label}
            </span>
        </div>
    );
}
