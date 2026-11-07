import { lazy, Suspense, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Bot } from "lucide-react";
import {
    subscribeMcpConfirmationClosed, subscribeMcpConfirmationRequested, subscribeMcpOpenProject, subscribeMcpState,
} from "@/api/events/subscriptions";
import { mcpApi } from "@/api/mcp/mcp-api";
import type { McpConfirmationResponseDto } from "@/api/mcp/response-dto/mcp-response-dto";
import { Button } from "@/components/ui/button";

// The dialogs and the mcp catalogs load with their own chunk, when the user opens the settings or an AI client asks for a confirmation.
const McpSettingsDialog = lazy(() => import("@/components/mcp/mcp-settings-dialog").then(module => ({ default: module.McpSettingsDialog })));
const McpConfirmationDialog = lazy(() => import("@/components/mcp/mcp-confirmation-dialog").then(module => ({ default: module.McpConfirmationDialog })));

/**
 * The header entry "AI assistants (MCP)" next to the language: opens the settings dialog and carries a status dot while the server runs. It also
 * shows the confirmations AI clients wait for (one at a time, oldest first) and passes open_project_in_app to the page.
 */
export function McpHeader({ disabled, openProject }: { disabled?: boolean; openProject: (projectId: string) => void }) {
    const { t } = useTranslation();
    const [open, setOpen] = useState(false);
    const [running, setRunning] = useState(false);
    const [pending, setPending] = useState<McpConfirmationResponseDto[]>([]);
    const opener = useRef(openProject);
    opener.current = openProject;

    useEffect(() => {
        let alive = true;
        mcpApi.getState().then(state => { if (alive) setRunning(state.status === "running"); }, () => { /* the page outside the application */ });
        mcpApi.listConfirmations().then(items => { if (alive) setPending(items); }, () => { /* none */ });
        const unsubscribe = [
            subscribeMcpState(state => setRunning(state.status === "running")),
            subscribeMcpConfirmationRequested(item => setPending(current => current.some(other => other.id === item.id) ? current : [...current, item])),
            subscribeMcpConfirmationClosed(closed => setPending(current => current.filter(item => item.id !== closed.id))),
        ];
        return () => {
            alive = false;
            unsubscribe.forEach(stop => stop());
        };
    }, []);

    useEffect(() => subscribeMcpOpenProject(request => opener.current(request.projectId)), []);

    const label = running ? t("app.aiAssistantsRunning") : t("app.aiAssistants");
    const current = pending[0];
    return (
        <>
            <Button size="icon-sm" variant="ghost" aria-label={label} title={label} disabled={disabled} className="relative"
                data-testid="mcp-settings-button" data-running={running} onClick={() => setOpen(true)}>
                <Bot />
                {/* The running server is a status dot; the label says it in words. */}
                {running && <span className="absolute top-1 right-1 size-1.5 rounded-full bg-ok shadow-[0_0_6px_var(--ok)]" aria-hidden />}
            </Button>
            <Suspense fallback={null}>
                {open && <McpSettingsDialog close={() => setOpen(false)} />}
                {current && (
                    <McpConfirmationDialog key={current.id} confirmation={current}
                        answered={id => setPending(items => items.filter(item => item.id !== id))} />
                )}
            </Suspense>
        </>
    );
}
