import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Copy, Eye, EyeOff, FolderPlus, RefreshCw, X } from "lucide-react";
import { subscribeMcpCall, subscribeMcpState } from "@/api/events/subscriptions";
import { mcpApi } from "@/api/mcp/mcp-api";
import type { McpAccessLevel, UpdateMcpSettingsRequestDto } from "@/api/mcp/request-dto/mcp-request-dto";
import type { McpCallResponseDto, McpStateResponseDto } from "@/api/mcp/response-dto/mcp-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import "@/i18n/sections/mcp";
import { errorMessage, formatDateTime, formatNumber } from "@/lib/format";
import { mcpClientConfigurations } from "@/lib/mcp/client-configurations";

const levels: readonly McpAccessLevel[] = ["read_only", "read_write", "full"];
const pageSize = 100;

/**
 * "AI assistants (MCP)": the server switch and its state, port, access level, token, ready configurations for clients, the optional folder
 * restriction and the call log. The host keeps the settings; no MCP tool can change them.
 */
export function McpSettingsDialog({ close }: { close: () => void }) {
    const { t } = useTranslation("mcp");
    const [state, setState] = useState<McpStateResponseDto>();
    const [error, setError] = useState<string>();
    const [busy, setBusy] = useState(false);
    const [port, setPort] = useState("");
    const [tokenShown, setTokenShown] = useState(false);
    const [copied, setCopied] = useState<string>();
    const [calls, setCalls] = useState<McpCallResponseDto[]>([]);
    const [more, setMore] = useState(false);

    useEffect(() => {
        const controller = new AbortController();
        mcpApi.getState(controller.signal).then(value => { setState(value); setPort(String(value.port)); }, cause => {
            if (!controller.signal.aborted) setError(errorMessage(cause));
        });
        mcpApi.listCalls({ limit: pageSize }, controller.signal).then(value => { setCalls(value); setMore(value.length === pageSize); }, () => { /* the log stays empty */ });
        const unsubscribeState = subscribeMcpState(value => setState(value));
        const unsubscribeCalls = subscribeMcpCall(call => setCalls(current => current.some(item => item.sequence === call.sequence) ? current : [call, ...current]));
        return () => {
            controller.abort();
            unsubscribeState();
            unsubscribeCalls();
        };
    }, []);

    async function run(action: () => Promise<McpStateResponseDto>) {
        setBusy(true);
        setError(undefined);
        try {
            const next = await action();
            setState(next);
            setPort(String(next.port));
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const update = (request: UpdateMcpSettingsRequestDto) => run(() => mcpApi.update(request));

    async function copy(key: string, text: string) {
        try {
            await navigator.clipboard.writeText(text);
            setCopied(key);
            window.setTimeout(() => setCopied(current => current === key ? undefined : current), 2000);
        } catch {
            setError(t("config.copyFailed"));
        }
    }

    async function loadMore() {
        const last = calls.at(-1);
        if (!last) return;
        try {
            const page = await mcpApi.listCalls({ limit: pageSize, before: last.sequence });
            setCalls(current => [...current, ...page]);
            setMore(page.length === pageSize);
        } catch (cause) {
            setError(errorMessage(cause));
        }
    }

    const portValue = Number(port);
    const portValid = /^\d+$/.test(port) && portValue >= 1024 && portValue <= 65535;
    const configurations = state?.token ? mcpClientConfigurations(state) : [];

    return (
        <Dialog open onOpenChange={open => { if (!open) close(); }}>
            <DialogContent data-testid="mcp-settings-dialog" className="max-h-[calc(100dvh-3rem)] overflow-y-auto sm:max-w-2xl">
                <DialogHeader>
                    <DialogTitle>{t("title")}</DialogTitle>
                    <DialogDescription>{t("description")}</DialogDescription>
                </DialogHeader>
                {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                {state && (
                    <div className="space-y-5 text-sm">
                        <section className="space-y-2">
                            <div className="flex items-center gap-2">
                                <Checkbox id="mcp-enabled" data-testid="mcp-enabled" checked={state.enabled} disabled={busy}
                                    onCheckedChange={value => void update({ enabled: value === true })} />
                                <Label htmlFor="mcp-enabled">{t("enable")}</Label>
                            </div>
                            <p data-testid="mcp-status" data-status={state.status}
                                className={state.status === "port_busy" || state.status === "failed" ? "text-destructive" : "text-muted-foreground"}>
                                {t(`status.${state.status}`, { endpoint: state.endpoint, port: state.port })}
                            </p>
                        </section>

                        <section className="grid gap-4 sm:grid-cols-2">
                            <div className="space-y-1.5">
                                <Label htmlFor="mcp-port">{t("port.label")}</Label>
                                <div className="flex gap-2">
                                    <Input id="mcp-port" data-testid="mcp-port" inputMode="numeric" value={port} disabled={busy} aria-invalid={!portValid}
                                        onChange={event => setPort(event.target.value.trim())} className="w-28" />
                                    <Button variant="outline" disabled={busy || !portValid || portValue === state.port} onClick={() => void update({ port: portValue })}>
                                        {t("port.apply")}
                                    </Button>
                                </div>
                                <p className={portValid ? "text-xs text-muted-foreground" : "text-xs text-destructive"}>{portValid ? t("port.hint") : t("port.invalid")}</p>
                            </div>
                            <div className="space-y-1.5">
                                <Label>{t("access.label")}</Label>
                                <Select value={state.accessLevel} disabled={busy} onValueChange={value => void update({ accessLevel: value as McpAccessLevel })}>
                                    <SelectTrigger data-testid="mcp-access" aria-label={t("access.label")} className="w-full"><SelectValue /></SelectTrigger>
                                    <SelectContent>
                                        {levels.map(level => <SelectItem key={level} value={level}>{t(`access.${level}`)}</SelectItem>)}
                                    </SelectContent>
                                </Select>
                                <p className="text-xs text-muted-foreground">{t(`access.hint.${state.accessLevel}`)}</p>
                            </div>
                        </section>

                        <section className="space-y-1.5">
                            <Label>{t("token.label")}</Label>
                            {state.token ? (
                                <div className="flex items-center gap-2">
                                    <code data-testid="mcp-token" className="min-w-0 flex-1 truncate rounded-md border bg-muted px-2 py-1.5 font-mono text-xs">
                                        {tokenShown ? state.token : "•".repeat(24)}
                                    </code>
                                    <Button size="icon-sm" variant="ghost" aria-label={tokenShown ? t("token.hide") : t("token.show")} title={tokenShown ? t("token.hide") : t("token.show")}
                                        onClick={() => setTokenShown(value => !value)}>
                                        {tokenShown ? <EyeOff /> : <Eye />}
                                    </Button>
                                    <Button size="icon-sm" variant="ghost" aria-label={t("token.copy")} title={copied === "token" ? t("config.copied") : t("token.copy")}
                                        onClick={() => void copy("token", state.token ?? "")}>
                                        <Copy />
                                    </Button>
                                    <Button size="icon-sm" variant="ghost" aria-label={t("token.regenerate")} title={t("token.regenerateHint")} disabled={busy}
                                        data-testid="mcp-regenerate" onClick={() => void run(() => mcpApi.regenerateToken())}>
                                        <RefreshCw />
                                    </Button>
                                </div>
                            ) : <p className="text-muted-foreground">{t("token.none")}</p>}
                        </section>

                        {configurations.length > 0 && (
                            <section className="space-y-2">
                                <h3 className="font-medium">{t("config.title")}</h3>
                                {configurations.map(item => (
                                    <div key={item.key} className="space-y-1">
                                        <div className="flex items-center justify-between gap-2">
                                            <span className="text-xs text-muted-foreground">{t(`config.${item.key}`)}</span>
                                            <Button size="xs" variant="outline" data-testid={`mcp-copy-${item.key}`} onClick={() => void copy(item.key, item.text)}>
                                                <Copy />{copied === item.key ? t("config.copied") : t("config.copy")}
                                            </Button>
                                        </div>
                                        <pre className="max-h-32 overflow-auto rounded-md border bg-muted p-2 font-mono text-xs whitespace-pre-wrap break-all">{tokenShown ? item.text : item.masked}</pre>
                                        {item.key === "claudeDesktop" && !state.proxyAvailable && <p className="text-xs text-muted-foreground">{t("config.proxyMissing")}</p>}
                                    </div>
                                ))}
                            </section>
                        )}

                        <section className="space-y-2">
                            <div className="flex items-center gap-2">
                                <Checkbox id="mcp-restrict" data-testid="mcp-restrict" checked={state.restrictFolders} disabled={busy}
                                    onCheckedChange={value => void update({ restrictFolders: value === true })} />
                                <Label htmlFor="mcp-restrict">{t("folders.restrict")}</Label>
                            </div>
                            <p className="text-xs text-muted-foreground">{t("folders.hint")}</p>
                            {state.restrictFolders && (
                                <div className="space-y-1.5">
                                    {state.allowedFolders.length === 0 && <p className="text-xs text-destructive">{t("folders.empty")}</p>}
                                    <ul className="space-y-1">
                                        {state.allowedFolders.map(folder => (
                                            <li key={folder} className="flex items-center gap-2">
                                                <span className="min-w-0 flex-1 truncate font-mono text-xs" title={folder}>{folder}</span>
                                                <Button size="icon-xs" variant="ghost" aria-label={t("folders.remove", { path: folder })} disabled={busy}
                                                    onClick={() => void run(() => mcpApi.removeAllowedFolder({ path: folder }))}>
                                                    <X />
                                                </Button>
                                            </li>
                                        ))}
                                    </ul>
                                    <Button size="sm" variant="outline" disabled={busy} onClick={() => void run(() => mcpApi.addAllowedFolder())}>
                                        <FolderPlus />{t("folders.add")}
                                    </Button>
                                </div>
                            )}
                        </section>

                        <section className="space-y-1">
                            <div className="flex items-center gap-2">
                                <Checkbox id="mcp-launch" checked={state.launchOnRequest} disabled={busy}
                                    onCheckedChange={value => void update({ launchOnRequest: value === true })} />
                                <Label htmlFor="mcp-launch">{t("launch.label")}</Label>
                            </div>
                            <p className="text-xs text-muted-foreground">{t("launch.hint")}</p>
                        </section>

                        <Alert><AlertDescription>{t("privacy")}</AlertDescription></Alert>

                        <section className="space-y-2">
                            <div>
                                <h3 className="font-medium">{t("log.title")}</h3>
                                <p className="text-xs text-muted-foreground">{t("log.hint")}</p>
                            </div>
                            {calls.length === 0 ? <p className="text-muted-foreground">{t("log.empty")}</p> : (
                                <ul data-testid="mcp-call-log" className="max-h-72 space-y-1 overflow-y-auto rounded-md border p-2">
                                    {calls.map(call => (
                                        <li key={call.sequence} className="space-y-0.5 border-b pb-1 text-xs last:border-b-0" data-tool={call.tool}>
                                            <div className="flex flex-wrap items-center gap-2">
                                                <span className="text-muted-foreground">{formatDateTime(call.time)}</span>
                                                <span className="font-medium">{call.client}</span>
                                                <code className="font-mono">{call.tool}</code>
                                                <Badge variant={call.succeeded ? "secondary" : "destructive"}>{call.succeeded ? t("log.succeeded") : call.error ?? t("log.failed")}</Badge>
                                                <span className="text-muted-foreground">{t("log.duration", { ms: formatNumber(call.durationMs) })}</span>
                                            </div>
                                            <code className="block truncate font-mono text-muted-foreground" title={call.arguments}>{call.arguments}</code>
                                            {call.result && <p className="truncate" title={call.result}>{call.result}</p>}
                                        </li>
                                    ))}
                                </ul>
                            )}
                            {more && <Button size="sm" variant="ghost" onClick={() => void loadMore()}>{t("log.more")}</Button>}
                        </section>
                    </div>
                )}
            </DialogContent>
        </Dialog>
    );
}
