import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import type { BackgroundSourcePreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { nudgeRegion, pointerRegion, type FramePosition } from "@/lib/backgrounds/crop-frame";

const percent = (part: number, whole: number) => `${(part / whole) * 100}%`;

/**
 * Рамка неизменного размера (размер набора) поверх кадра. Мышью задаётся только положение: центр рамки идёт за
 * указателем; стрелки двигают на 1 px, с Shift — на 10 px. Картинка на экране — уменьшенная копия (до 1600 px),
 * поэтому всё считается в пикселях исходного кадра (source.width × source.height), а на экран переводится долями.
 */
export function BackgroundCropFrame({ source, frame, value, onChange, disabled, autoFocus, maxHeight = "58vh" }: {
    source: BackgroundSourcePreviewResponseDto;
    frame: InputSizeDto;
    value: FramePosition;
    onChange: (value: FramePosition) => void;
    disabled?: boolean;
    /** Фокус на рамке при показе кадра: стрелки сразу двигают её. */
    autoFocus?: boolean;
    /** Предел высоты кадра на экране. */
    maxHeight?: string;
}) {
    const surface = useRef<HTMLDivElement>(null);
    const [dragging, setDragging] = useState(false);
    const [broken, setBroken] = useState<string>();

    useEffect(() => {
        if (autoFocus) surface.current?.focus();
    }, [autoFocus, source.previewUrl]);

    function follow(event: PointerEvent<HTMLDivElement>) {
        const box = surface.current?.getBoundingClientRect();
        const next = box && pointerRegion(event.clientX, event.clientY, box, source, frame);
        if (next) onChange(next);
    }

    function nudge(event: KeyboardEvent<HTMLDivElement>) {
        if (disabled) return;
        const next = nudgeRegion(value, event.key, event.shiftKey, source, frame);
        if (!next) return;
        event.preventDefault();
        onChange(next);
    }

    return (
        <div className="space-y-2">
            <div
                ref={surface}
                role="slider"
                tabIndex={disabled ? -1 : 0}
                aria-label="Положение рамки на кадре"
                aria-valuetext={`x ${value.left}, y ${value.top}`}
                aria-valuenow={value.left}
                aria-valuemin={0}
                aria-valuemax={Math.max(0, source.width - frame.width)}
                aria-disabled={disabled || undefined}
                data-testid="crop-frame"
                data-left={value.left}
                data-top={value.top}
                className={`relative mx-auto touch-none overflow-hidden rounded-md bg-muted outline-none select-none focus-visible:ring-2 focus-visible:ring-ring ${disabled ? "opacity-60" : dragging ? "cursor-grabbing" : "cursor-crosshair"}`}
                style={{ aspectRatio: `${source.width} / ${source.height}`, width: `min(100%, calc(${maxHeight} * ${source.width / source.height}))` }}
                onPointerDown={event => {
                    if (disabled || event.button !== 0) return;
                    event.currentTarget.setPointerCapture(event.pointerId);
                    event.currentTarget.focus();
                    setDragging(true);
                    follow(event);
                }}
                onPointerMove={event => { if (dragging) follow(event); }}
                onPointerUp={() => setDragging(false)}
                onPointerCancel={() => setDragging(false)}
                onKeyDown={nudge}
            >
                {broken === source.previewUrl ? (
                    <div className="absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">Не удалось показать кадр.</div>
                ) : (
                    <img src={source.previewUrl} alt={source.name} draggable={false} className="absolute inset-0 block size-full" data-testid="crop-image" onError={() => setBroken(source.previewUrl)} />
                )}
                <div
                    className="pointer-events-none absolute border-2 border-primary shadow-[0_0_0_9999px_rgba(0,0,0,0.55)]"
                    style={{ left: percent(value.left, source.width), top: percent(value.top, source.height), width: percent(frame.width, source.width), height: percent(frame.height, source.height) }}
                />
            </div>
            <p className="text-center text-xs text-muted-foreground tabular-nums" data-testid="crop-position">
                Рамка {frame.width} × {frame.height} px · x {value.left}, y {value.top} · кадр {source.width} × {source.height}
            </p>
        </div>
    );
}
