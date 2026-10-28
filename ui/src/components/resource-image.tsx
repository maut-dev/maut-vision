import { useRef, useState } from "react";
import { ImageOff } from "lucide-react";
import type { ResourceUrl } from "@/api/resources/resource-url";
import { cn } from "@/lib/utils";

type Status = "loading" | "loaded" | "failed";

/**
 * Image of a project file from the host resource channel. Bytes never travel through the bridge: the DTO carries
 * a ResourceUrl and the WebView loads it lazily. A failed load usually means the URL expired or the page was
 * reloaded, so the image asks the owner once for a fresh DTO (onExpired) before showing the failure state.
 * width and height reserve the box before loading so a gallery does not shift.
 */
export function ResourceImage({ src, alt, width, height, fit = "cover", className, onExpired }: {
    src: ResourceUrl | null;
    alt: string;
    width?: number;
    height?: number;
    fit?: "cover" | "contain";
    className?: string;
    onExpired?: () => void;
}) {
    const [state, setState] = useState<{ src: ResourceUrl | null; status: Status }>({ src, status: "loading" });
    const refreshed = useRef(false);
    // A new URL starts loading again without an extra render.
    const status = state.src === src ? state.status : "loading";

    function onLoad() {
        refreshed.current = false;
        setState({ src, status: "loaded" });
    }

    function onError() {
        if (onExpired && !refreshed.current) {
            refreshed.current = true;
            onExpired();
            return;
        }
        setState({ src, status: "failed" });
    }

    return (
        <div
            className={cn("relative overflow-hidden rounded-md bg-muted", className)}
            style={width && height ? { aspectRatio: `${width} / ${height}` } : undefined}
            data-status={src === null ? "failed" : status}
        >
            {src !== null && status !== "failed" && (
                <img
                    key={src}
                    src={src}
                    alt={alt}
                    width={width}
                    height={height}
                    loading="lazy"
                    decoding="async"
                    draggable={false}
                    onLoad={onLoad}
                    onError={onError}
                    className={cn(
                        "size-full transition-opacity duration-150",
                        fit === "cover" ? "object-cover" : "object-contain",
                        status === "loaded" ? "opacity-100" : "opacity-0",
                    )}
                />
            )}
            {(src === null || status === "failed") && (
                <div className="absolute inset-0 flex items-center justify-center text-muted-foreground" role="img" aria-label={alt}>
                    <ImageOff className="size-5" aria-hidden="true" />
                </div>
            )}
        </div>
    );
}
