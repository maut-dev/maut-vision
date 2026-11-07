import type { ReactNode } from "react";
import { useWindowState, WindowControls } from "@/components/shell/window-controls";

/**
 * The header of the shell is the title bar of the frameless window: its background drags the window (app-region: drag; a double click
 * maximizes, a right click opens the system menu), the controls inside stay clickable. Left: the mark, the name and the navigation of
 * the page; right: its actions, a divider and the window buttons flush with the top right corner. The spacer between the groups keeps an
 * empty drag area at any window width.
 */
export function TitleBar({ navigation, actions, failed }: { navigation: ReactNode; actions: ReactNode; failed: (message: string) => void }) {
    const state = useWindowState();
    return (
        <header className="glass drag-region relative z-10 flex h-11 shrink-0 items-stretch border-x-0 border-t-0" data-testid="title-bar"
            data-active={state.active}>
            <div className="flex min-w-0 items-center gap-2.5 pl-3">
                <img src="./brand/logo-mark.png" alt="Maut Vision" className="size-6 shrink-0 object-contain" draggable={false} />
                <span className="hidden text-[13px] font-semibold tracking-tight whitespace-nowrap md:inline">Maut Vision</span>
                <div className="h-5 shrink-0 border-l border-line-strong" aria-hidden />
                {navigation}
            </div>
            <div className="min-w-6 flex-1" data-testid="title-bar-drag" aria-hidden />
            <div className="flex shrink-0 items-center gap-1 pr-2">{actions}</div>
            <div className="my-auto h-5 shrink-0 border-l border-line" aria-hidden />
            <WindowControls state={state} failed={failed} />
        </header>
    );
}
