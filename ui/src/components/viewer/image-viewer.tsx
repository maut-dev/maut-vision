import { useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ArrowLeft, ArrowRight, Circle } from "lucide-react";
import type { ResourceUrl } from "@/api/resources/resource-url";
import { useReturnFocus } from "@/components/catalog/catalog-dialogs";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import type { Box } from "@/lib/annotation/geometry";
import { isTypingTarget, useZoomViewport } from "./use-zoom-viewport";
import { ZoomControls } from "./zoom-controls";

export interface ViewerOverlay {
    id: string;
    label: string;
    color: string;
    /** negative — the box of a dataset negative, dashed like an ROI. */
    kind: "object" | "negative" | "roi";
    /** Fractions 0–1 from the top left corner of the frame. */
    box: Box;
}

/**
 * What the viewer shows. width/height — the frame size with the orientation applied; src — a URL of the resource channel. A URL
 * is valid for a limited time: on a load error the viewer asks the owner once to renew the source (renew).
 */
export interface ViewerSource {
    id: string;
    name: string;
    width: number;
    height: number;
    src: ResourceUrl | null;
    renew?: () => void;
    overlays?: readonly ViewerOverlay[];
    /** Details under the frame: sizes, source, annotation, toggles. */
    metadata?: ReactNode;
}

type LoadState = "loading" | "loaded" | "failed";

/**
 * The shared frame viewer: Fit without enlarging a small frame, x1…x10, dragging, full screen, boxes
 * over the frame with a legend. Esc in full screen first leaves it, then closes; focus returns to
 * the element that opened the viewer. Previous/Next and loading pages are done by the owner.
 */
export function ImageViewer({ source, close, onPrevious, onNext, navigationBusy = false, position }: {
    source: ViewerSource;
    close: () => void;
    onPrevious?: () => void;
    onNext?: () => void;
    navigationBusy?: boolean;
    /** "i / n", or "i / n+" when there are more pages. */
    position?: string;
}) {
    const { t } = useTranslation();
    const [fullscreen, setFullscreen] = useState(false);
    const [load, setLoad] = useState<{ src: ResourceUrl | null; state: LoadState }>({ src: source.src, state: "loading" });
    const [attempt, setAttempt] = useState(0);
    const [selected, setSelected] = useState<{ source: string; overlay: string }>();
    const renewed = useRef<string | undefined>(undefined);
    const returnFocus = useReturnFocus();
    const zoom = useZoomViewport(source, source.id);
    const state: LoadState = source.src === null ? "failed" : load.src === source.src ? load.state : "loading";
    const selectedOverlay = selected?.source === source.id ? selected.overlay : undefined;

    function failed() {
        if (source.renew && renewed.current !== source.id) {
            renewed.current = source.id;
            source.renew();
            return;
        }
        setLoad({ src: source.src, state: "failed" });
    }

    function retry() {
        renewed.current = undefined;
        setLoad({ src: source.src, state: "loading" });
        setAttempt(value => value + 1);
        source.renew?.();
    }

    const go = (action?: () => void) => { if (action && !navigationBusy) action(); };

    return (
        <Dialog open onOpenChange={open => { if (!open) close(); }}>
            <DialogContent
                data-image-viewer
                data-viewer-fullscreen={fullscreen}
                className="flex min-h-0 flex-col gap-3 overflow-hidden p-4 sm:max-w-none"
                style={{
                    width: fullscreen ? "100vw" : "min(1200px, calc(100vw - 32px))",
                    maxWidth: fullscreen ? "100vw" : "calc(100vw - 32px)",
                    height: fullscreen ? "100dvh" : "min(900px, calc(100dvh - 32px))",
                    maxHeight: fullscreen ? "100dvh" : "calc(100dvh - 32px)",
                    borderRadius: fullscreen ? 0 : undefined,
                }}
                onCloseAutoFocus={returnFocus}
                onEscapeKeyDown={event => {
                    if (fullscreen) {
                        event.preventDefault();
                        setFullscreen(false);
                    }
                }}
                onKeyDown={event => {
                    if (zoom.handleZoomKey(event) || isTypingTarget(event.target)) return;
                    if (event.key === "ArrowLeft" && onPrevious) { event.preventDefault(); go(onPrevious); }
                    if (event.key === "ArrowRight" && onNext) { event.preventDefault(); go(onNext); }
                }}
            >
                <DialogTitle className="truncate pr-8 text-base">{source.name}</DialogTitle>
                <DialogDescription className="text-xs">
                    {t("viewer.hint", { width: source.width, height: source.height })}
                </DialogDescription>
                <div className="flex flex-wrap items-center gap-2">
                    <Button size="sm" variant="outline" disabled={!onPrevious || navigationBusy} onClick={() => go(onPrevious)}><ArrowLeft aria-hidden />{t("viewer.previous")}</Button>
                    {position && <span className="text-xs tabular-nums text-muted-foreground" data-testid="viewer-position">{position}</span>}
                    <Button size="sm" variant="outline" disabled={!onNext || navigationBusy} onClick={() => go(onNext)}>{t("viewer.next")}<ArrowRight aria-hidden /></Button>
                    <ZoomControls zoom={zoom} />
                    <Button size="sm" variant="outline" onClick={() => setFullscreen(value => !value)}>
                        {fullscreen ? t("viewer.exitFullscreen") : t("viewer.fullscreen")}
                    </Button>
                </div>
                <div
                    ref={zoom.ref}
                    data-viewer-viewport
                    role="region"
                    aria-label={t("viewer.viewport")}
                    tabIndex={0}
                    aria-busy={state === "loading"}
                    className="relative min-h-0 flex-1 cursor-grab overflow-auto overscroll-contain rounded-md border bg-muted/30 outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    style={{ touchAction: "none" }}
                    onPointerDown={event => { if (event.button === 0 && state === "loaded") zoom.beginPan(event); }}
                    onPointerMove={event => { zoom.movePan(event); }}
                    onPointerUp={() => zoom.endPan()}
                    onPointerCancel={() => zoom.endPan()}
                    onLostPointerCapture={() => zoom.endPan()}
                >
                    {source.src !== null && state !== "failed" && (
                        <div style={{ width: zoom.canvas.width, height: zoom.canvas.height, position: "relative" }}>
                            <div
                                style={{
                                    position: "absolute",
                                    left: zoom.offset.left,
                                    top: zoom.offset.top,
                                    width: zoom.frame.width,
                                    height: zoom.frame.height,
                                    visibility: state === "loaded" ? "visible" : "hidden",
                                }}
                            >
                                <img
                                    key={`${source.src}#${attempt}`}
                                    data-viewer-image
                                    src={source.src}
                                    alt={source.name}
                                    draggable={false}
                                    className="block size-full max-w-none"
                                    onLoad={() => setLoad({ src: source.src, state: "loaded" })}
                                    onError={failed}
                                />
                                <svg viewBox="0 0 1000 1000" preserveAspectRatio="none" className="pointer-events-none absolute inset-0 size-full" aria-hidden>
                                    {source.overlays?.map(overlay => (
                                        <rect
                                            key={overlay.id}
                                            data-viewer-overlay={overlay.id}
                                            x={overlay.box.x * 1000}
                                            y={overlay.box.y * 1000}
                                            width={overlay.box.width * 1000}
                                            height={overlay.box.height * 1000}
                                            fill={selectedOverlay === overlay.id ? `${overlay.color}33` : "none"}
                                            stroke={overlay.color}
                                            strokeWidth={selectedOverlay === overlay.id ? 3 : 2}
                                            vectorEffect="non-scaling-stroke"
                                            strokeDasharray={overlay.kind === "roi" ? "6 4" : overlay.kind === "negative" ? "4 3" : undefined}
                                        />
                                    ))}
                                </svg>
                                {source.overlays?.map((overlay, index) => (
                                    <span
                                        key={overlay.id}
                                        className="pointer-events-none absolute max-w-48 truncate rounded-sm bg-black/80 px-1 text-[11px] text-white"
                                        style={{ left: `${overlay.box.x * 100}%`, top: `${overlay.box.y * 100}%`, borderLeft: `2px solid ${overlay.color}` }}
                                    >
                                        {overlay.kind === "roi" ? overlay.label : `${index + 1}. ${overlay.label}`}
                                    </span>
                                ))}
                            </div>
                        </div>
                    )}
                    {state !== "loaded" && (
                        <div className="absolute inset-0 flex items-center justify-center p-4 text-center text-sm text-muted-foreground" role={state === "failed" ? "alert" : "status"}>
                            {state === "failed" ? (
                                <div className="space-y-3">
                                    <p>{t("viewer.unavailable")}</p>
                                    <Button variant="outline" onClick={retry}>{t("common.retryLoading")}</Button>
                                </div>
                            ) : t("viewer.loading")}
                        </div>
                    )}
                </div>
                {(source.metadata || !!source.overlays?.length) && (
                    <div className="max-h-32 shrink-0 overflow-auto text-xs text-muted-foreground">
                        {source.metadata}
                        {!!source.overlays?.length && (
                            <div className="mt-2 flex flex-wrap gap-2" aria-label={t("viewer.legend")}>
                                {source.overlays.map((overlay, index) => (
                                    <button
                                        key={overlay.id}
                                        type="button"
                                        aria-pressed={selectedOverlay === overlay.id}
                                        className="inline-flex items-center gap-1 rounded border px-2 py-1 text-left outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring aria-pressed:bg-accent"
                                        onClick={() => setSelected(selectedOverlay === overlay.id ? undefined : { source: source.id, overlay: overlay.id })}
                                    >
                                        <Circle aria-hidden className="size-2 fill-current" style={{ color: overlay.color }} />
                                        {overlay.kind === "roi" ? overlay.label : `${index + 1}. ${overlay.label}`}
                                    </button>
                                ))}
                            </div>
                        )}
                    </div>
                )}
            </DialogContent>
        </Dialog>
    );
}
