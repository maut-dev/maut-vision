import { useEffect, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { subscribeWindowStateChanged } from "@/api/events/subscriptions";
import type { WindowStateResponseDto } from "@/api/window/response-dto/window-state-response-dto";
import { windowApi } from "@/api/window/window-api";
import { errorMessage } from "@/lib/format";
import { cn } from "@/lib/utils";

/**
 * The state of the main window: the initial one from the host, then window.stateChanged (also for Win+Up, snapping and a double click on
 * the drag area). Outside the application the window counts as normal and active.
 */
export function useWindowState(): WindowStateResponseDto {
    const [state, setState] = useState<WindowStateResponseDto>({ maximized: false, active: true });
    useEffect(() => {
        let alive = true;
        let changed = false;
        windowApi.getState().then(value => { if (alive && !changed) setState(value); }, () => { /* the page outside the application */ });
        const unsubscribe = subscribeWindowStateChanged(event => {
            changed = true;
            setState(event.data);
        });
        return () => {
            alive = false;
            unsubscribe();
        };
    }, []);
    return state;
}

const buttonClasses = "no-drag inline-flex h-full w-[46px] shrink-0 items-center justify-center text-text-2 outline-none transition-colors duration-120 "
    + "ease-glass hover:bg-glass-fill-hover hover:text-text-1 focus-visible:bg-glass-fill-hover focus-visible:text-text-1 "
    + "focus-visible:shadow-[inset_0_0_0_2px_var(--focus)] data-[inactive=true]:text-text-3";

/** Glyphs of the Windows caption buttons: 10 px, a 1 px line. */
function Glyph({ children, crisp = true }: { children: ReactNode; crisp?: boolean }) {
    return (
        <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1" shapeRendering={crisp ? "crispEdges" : "geometricPrecision"}
            aria-hidden>
            {children}
        </svg>
    );
}

/**
 * Minimize, maximize or restore, and close at the right edge of the title bar, as Windows draws them: 46 px wide and as tall as the bar.
 * Close hides the window in the notification area, as the system button did; the buttons dim while the window is inactive.
 */
export function WindowControls({ state, failed }: { state: WindowStateResponseDto; failed: (message: string) => void }) {
    const { t } = useTranslation();
    const run = (command: () => Promise<unknown>) => void command().catch(cause => failed(errorMessage(cause)));
    const maximizeLabel = state.maximized ? t("window.restore") : t("window.maximize");
    const inactive = !state.active;
    return (
        <div className="flex h-full shrink-0 items-stretch" data-testid="window-controls">
            <button type="button" className={buttonClasses} data-inactive={inactive} data-testid="window-minimize"
                aria-label={t("window.minimize")} title={t("window.minimize")} onClick={() => run(() => windowApi.minimize())}>
                <Glyph><path d="M0 5.5h10" /></Glyph>
            </button>
            <button type="button" className={buttonClasses} data-inactive={inactive} data-testid="window-maximize" data-maximized={state.maximized}
                aria-label={maximizeLabel} title={maximizeLabel} onClick={() => run(() => windowApi.toggleMaximize())}>
                {state.maximized
                    ? <Glyph><path d="M0.5 2.5h7v7h-7z M2.5 2.5v-2h7v7h-2" /></Glyph>
                    : <Glyph><path d="M0.5 0.5h9v9h-9z" /></Glyph>}
            </button>
            <button type="button" className={cn(buttonClasses, "hover:bg-[#c42b1c] hover:text-white active:bg-[#c42b1c]/90 focus-visible:bg-[#c42b1c] focus-visible:text-white")}
                data-inactive={inactive} data-testid="window-close" aria-label={t("window.close")} title={t("window.close")} onClick={() => run(() => windowApi.close())}>
                <Glyph crisp={false}><path d="M0.5 0.5l9 9 M9.5 0.5l-9 9" /></Glyph>
            </button>
        </div>
    );
}
