import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { useTranslation } from "react-i18next";
import type { BackgroundSourcePreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { InputSizeDto } from "@/api/projects/input-size-dto";
import { nudgeRegion, pointerRegion, type FramePosition } from "@/lib/backgrounds/crop-frame";
import "@/i18n/sections/backgrounds";

const percent = (part: number, whole: number) => `${(part / whole) * 100}%`;

/**
 * A frame of a fixed size (the set size) over the image. The mouse sets only the position: the frame center follows the pointer;
 * arrows move it by 1 px, with Shift by 10 px. The image on screen is a reduced copy (up to 1600 px), so everything is counted in pixels
 * of the source image (source.width × source.height) and converted to the screen by fractions.
 */
export function BackgroundCropFrame({ source, frame, value, onChange, disabled, autoFocus, maxHeight = "58vh" }: {
    source: BackgroundSourcePreviewResponseDto;
    frame: InputSizeDto;
    value: FramePosition;
    onChange: (value: FramePosition) => void;
    disabled?: boolean;
    /** Focus on the frame when the image shows: arrows move it at once. */
    autoFocus?: boolean;
    /** The height limit of the image on screen. */
    maxHeight?: string;
}) {
    const { t } = useTranslation("backgrounds");
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
                aria-label={t("frame.label")}
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
                    <div className="absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">{t("frame.broken")}</div>
                ) : (
                    <img src={source.previewUrl} alt={source.name} draggable={false} className="absolute inset-0 block size-full" data-testid="crop-image" onError={() => setBroken(source.previewUrl)} />
                )}
                <div
                    className="pointer-events-none absolute border-2 border-primary shadow-[0_0_0_9999px_rgba(0,0,0,0.55)]"
                    style={{ left: percent(value.left, source.width), top: percent(value.top, source.height), width: percent(frame.width, source.width), height: percent(frame.height, source.height) }}
                />
            </div>
            <p className="text-center text-xs text-muted-foreground tabular-nums" data-testid="crop-position">
                {t("frame.position", { width: frame.width, height: frame.height, left: value.left, top: value.top, sourceWidth: source.width, sourceHeight: source.height })}
            </p>
        </div>
    );
}
