import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { mcpApi } from "@/api/mcp/mcp-api";
import type { McpConfirmationResponseDto } from "@/api/mcp/response-dto/mcp-response-dto";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import "@/i18n/sections/mcp";
import { i18n } from "@/i18n";

/** The text of a confirmation: a known action from the catalog with its values, an unknown one by its key. */
function actionText(confirmation: McpConfirmationResponseDto): string {
    const translate = i18n.t.bind(i18n) as unknown as (key: string, options?: Record<string, unknown>) => string;
    const key = `mcp:confirm.actions.${confirmation.action}`;
    return i18n.exists(key) ? translate(key, confirmation.values) : translate("mcp:confirm.unknown", { action: confirmation.action });
}

/**
 * A question of an AI client before it acts (deleting a project): who asks, with which tool and what happens. "Allow" and "Deny" answer the
 * host; without an answer the host denies it after two minutes and closes the dialog.
 */
export function McpConfirmationDialog({ confirmation, answered }: { confirmation: McpConfirmationResponseDto; answered: (id: string) => void }) {
    const { t } = useTranslation("mcp");
    const [busy, setBusy] = useState(false);
    const [now, setNow] = useState(() => Date.now());

    useEffect(() => {
        const timer = window.setInterval(() => setNow(Date.now()), 1000);
        return () => window.clearInterval(timer);
    }, []);

    async function answer(allow: boolean) {
        setBusy(true);
        try {
            await mcpApi.answerConfirmation({ id: confirmation.id, allow });
        } catch {
            // The question timed out or the server stopped: its dialog closes anyway.
        } finally {
            setBusy(false);
            answered(confirmation.id);
        }
    }

    const seconds = Math.max(0, Math.round((Date.parse(confirmation.expiresAt) - now) / 1000));
    const jobs = Number(confirmation.values.jobs ?? "0");
    return (
        <AlertDialog open onOpenChange={open => { if (!open && !busy) void answer(false); }}>
            <AlertDialogContent data-testid="mcp-confirmation" data-action={confirmation.action}>
                <AlertDialogHeader>
                    <AlertDialogTitle>{t("confirm.title")}</AlertDialogTitle>
                    <AlertDialogDescription asChild>
                        <div className="space-y-2">
                            <p>{actionText(confirmation)}</p>
                            {confirmation.action === "delete_project" && jobs > 0 && <p>{t("confirm.actions.delete_project_jobs", { jobs: confirmation.values.jobs })}</p>}
                            <p className="text-xs">{t("confirm.client", { client: confirmation.client })} · {t("confirm.tool", { tool: confirmation.tool })}</p>
                            <p className="text-xs">{t("confirm.expires", { seconds })}</p>
                        </div>
                    </AlertDialogDescription>
                </AlertDialogHeader>
                <AlertDialogFooter>
                    <AlertDialogCancel data-testid="mcp-confirmation-deny" disabled={busy} onClick={event => { event.preventDefault(); void answer(false); }}>
                        {t("confirm.deny")}
                    </AlertDialogCancel>
                    <AlertDialogAction data-testid="mcp-confirmation-allow" disabled={busy} onClick={event => { event.preventDefault(); void answer(true); }}>
                        {t("confirm.allow")}
                    </AlertDialogAction>
                </AlertDialogFooter>
            </AlertDialogContent>
        </AlertDialog>
    );
}
