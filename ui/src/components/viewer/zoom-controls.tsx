import { Minus, Plus } from "lucide-react";
import { maxZoom } from "@/lib/annotation/zoom";
import { Button } from "@/components/ui/button";
import type { ZoomViewport } from "./use-zoom-viewport";

/** «Вписать / сброс», «Уменьшить», выбор «Вписать (NN%)» и x1…x10, «Увеличить». */
export function ZoomControls({ zoom }: { zoom: ZoomViewport }) {
    return (
        <>
            <Button size="sm" variant={zoom.mode === "fit" ? "secondary" : "outline"} onClick={zoom.reset}>Вписать / сброс</Button>
            <Button size="sm" variant="outline" aria-label="Уменьшить" title="Уменьшить" onClick={zoom.zoomOut}><Minus aria-hidden /></Button>
            <select
                aria-label="Масштаб изображения"
                data-testid="zoom-select"
                value={zoom.mode}
                onChange={event => zoom.zoom(event.target.value === "fit" ? "fit" : Number(event.target.value))}
                className="h-8 rounded-md border bg-background px-2 text-sm tabular-nums outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
            >
                <option value="fit">Вписать ({Math.round(zoom.fit * 100)}%)</option>
                {Array.from({ length: maxZoom }, (_, index) => <option key={index + 1} value={index + 1}>x{index + 1}</option>)}
            </select>
            <Button size="sm" variant="outline" aria-label="Увеличить" title="Увеличить" disabled={zoom.scale >= maxZoom} onClick={zoom.zoomIn}><Plus aria-hidden /></Button>
        </>
    );
}
