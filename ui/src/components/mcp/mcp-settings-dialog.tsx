import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Copy, Eye, EyeOff, FolderPlus, RefreshCw, X } from "lucide-react";
import { subscribeMcpCall, subscribeMcpState } from "@/api/events/subscriptions";
import { mcpApi } from "@/api/mcp/mcp-api";
import type { McpAccessLevel, UpdateMcpSettingsRequestDto } from "@/api/mcp/request-dto/mcp-request-dto";
import type { McpCallResponseDto, McpServerStatus, McpStateResponseDto } from "@/api/mcp/response-dto/mcp-response-dto";
import { Collapsible } from "@/components/layout/collapsible";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/layout/tabs";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogBody, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import "@/i18n/sections/mcp";
import { errorText } from "@/lib/errors";
import { errorMessage, formatDateTime, formatNumber } from "@/lib/format";
import { mcpClientConfigurations } from "@/lib/mcp/client-configurations";
import type { StateBadgeVariant } from "@/lib/status-badge";

const levels: readonly McpAccessLevel[] = ["read_only", "read_write", "full"];
const pageSize = 100;
type Tab = "server" | "clients" | "log";

const statusBadge: Record<McpServerStatus, StateBadgeVariant> = {
    stopped: "plain",
    starting: "info",
    running: "ok",
    port_busy: "warn",
    failed: "danger",
};

/**
 * "AI assistants (MCP)": a fixed header with the server switch, its state and endpoint, then the tabs Server (port, access level, token,
 * folder restriction, launch on request), Clients (ready configurations) and Log (the calls). Each tab is the only scroll of the dialog and
 * stays mounted, so the shown token and "Copied" survive switching. The host keeps the settings; no MCP tool can change them.
 */
export function McpSettingsDialog({ close }: { close: () => void }) {
    const { t } = useTranslation("mcp");
    const [state, setState] = useState<McpStateResponseDto>();
    const [error, setError] = useState<string>();
    const [busy, setBusy] = useState(false);
    const [tab, setTab] = useState<Tab>("server");
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
    const problem = state?.status === "port_busy" || state?.status === "failed";

    return (
        <Dialog open onOpenChange={open => { if (!open) close(); }}>
            <DialogContent data-testid="mcp-settings-dialog" className="h-[min(640px,calc(100dvh-3rem))] sm:max-w-2xl">
                <DialogHeader>
                    <DialogTitle>{t("title")}</DialogTitle>
                    <DialogDescription>{t("description")}</DialogDescription>
                </DialogHeader>
                {state && (
                    <div className="flex shrink-0 flex-wrap items-center gap-x-4 gap-y-2 text-sm">
                        <div className="flex items-center gap-2">
                            <Checkbox id="mcp-enabled" data-testid="mcp-enabled" checked={state.enabled} disabled={busy}
                                onCheckedChange={value => void update({ enabled: value === true })} />
                            <Label htmlFor="mcp-enabled">{t("enable")}</Label>
                        </div>
                        <div data-testid="mcp-status" data-status={state.status} role="status" className="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-1">
                            <Badge variant={statusBadge[state.status]}>{t(`state.${state.status}`)}</Badge>
                            {state.status !== "stopped" && (
                                <code className="min-w-0 font-mono text-xs text-text-2 [overflow-wrap:anywhere]" title={t("endpoint")}>{state.endpoint}</code>
                            )}
                            {problem && <p className="w-full text-xs text-danger">{t(`status.${state.status as "port_busy" | "failed"}`, { port: state.port })}</p>}
                        </div>
                    </div>
                )}
                {error && <Alert variant="destructive" className="shrink-0"><AlertDescription>{error}</AlertDescription></Alert>}
                {state && (
                    <Tabs value={tab} onValueChange={value => setTab(value as Tab)} className="flex-1">
                        <TabsList aria-label={t("title")}>
                            <TabsTrigger value="server" data-testid="mcp-tab-server">{t("tabs.server")}</TabsTrigger>
                            <TabsTrigger value="clients" data-testid="mcp-tab-clients">{t("tabs.clients")}</TabsTrigger>
                            <TabsTrigger value="log" data-testid="mcp-tab-log" count={calls.length}>{t("tabs.log")}</TabsTrigger>
                        </TabsList>

                        <TabsContent value="server" forceMount hidden={tab !== "server"} asChild>
                            <DialogBody className="space-y-5 text-sm">
                                <section className="grid gap-4 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                                    <div className="min-w-0 space-y-1.5">
                                        <Label htmlFor="mcp-port">{t("port.label")}</Label>
                                        <div className="flex gap-2">
                                            <Input id="mcp-port" data-testid="mcp-port" inputMode="numeric" value={port} disabled={busy} aria-invalid={!portValid}
                                                onChange={event => setPort(event.target.value.trim())} className="w-28" />
                                            <Button variant="outline" disabled={busy || !portValid || portValue === state.port} onClick={() => void update({ port: portValue })}>
                                                {t("port.apply")}
                                            </Button>
                                        </div>
                                        <p className={portValid ? "text-xs text-text-2" : "text-xs text-danger"}>{portValid ? t("port.hint") : t("port.invalid")}</p>
                                    </div>
                                    <div className="min-w-0 space-y-1.5">
                                        <Label>{t("access.label")}</Label>
                                        <Select value={state.accessLevel} disabled={busy} onValueChange={value => void update({ accessLevel: value as McpAccessLevel })}>
                                            <SelectTrigger data-testid="mcp-access" aria-label={t("access.label")} className="w-full"><SelectValue /></SelectTrigger>
                                            <SelectContent>
                                                {levels.map(level => <SelectItem key={level} value={level}>{t(`access.${level}`)}</SelectItem>)}
                                            </SelectContent>
                                        </Select>
                                        <p className="text-xs text-text-2">{t(`access.hint.${state.accessLevel}`)}</p>
                                    </div>
                                </section>

                                <section className="space-y-1.5">
                                    <Label>{t("token.label")}</Label>
                                    {state.token ? (
                                        <div className="flex items-center gap-2">
                                            <code data-testid="mcp-token" className="min-w-0 flex-1 truncate rounded-md border border-line bg-ink-2 px-2 py-1.5 font-mono text-xs">
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
                                    ) : <p className="text-text-2">{t("token.none")}</p>}
                                </section>

                                <section className="space-y-2">
                                    <div className="flex items-center gap-2">
                                        <Checkbox id="mcp-restrict" data-testid="mcp-restrict" checked={state.restrictFolders} disabled={busy}
                                            onCheckedChange={value => void update({ restrictFolders: value === true })} />
                                        <Label htmlFor="mcp-restrict">{t("folders.restrict")}</Label>
                                    </div>
                                    <p className="text-xs text-text-2">{t("folders.hint")}</p>
                                    {state.restrictFolders && (
                                        <div className="space-y-1.5">
                                            {state.allowedFolders.length === 0 && <p className="text-xs text-danger">{t("folders.empty")}</p>}
                                            <ul className="space-y-1">
                                                {state.allowedFolders.map(folder => (
                                                    <li key={folder} className="flex items-center gap-2">
                                                        <span className="min-w-0 flex-1 font-mono text-xs [overflow-wrap:anywhere]">{folder}</span>
                                                        <Button size="icon-xs" variant="ghost" aria-label={t("folders.remove", { path: folder })} title={t("folders.remove", { path: folder })}
                                                            disabled={busy} onClick={() => void run(() => mcpApi.removeAllowedFolder({ path: folder }))}>
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
                                    <p className="text-xs text-text-2">{t("launch.hint")}</p>
                                </section>

                                <Alert><AlertDescription>{t("privacy")}</AlertDescription></Alert>
                            </DialogBody>
                        </TabsContent>

                        <TabsContent value="clients" forceMount hidden={tab !== "clients"} asChild>
                            <DialogBody className="space-y-4 text-sm">
                                {configurations.length === 0 ? <p className="text-text-2">{t("config.none")}</p> : configurations.map(item => (
                                    <section key={item.key} className="space-y-1.5">
                                        <div className="flex items-center justify-between gap-2">
                                            <h3 className="min-w-0 text-xs font-medium text-text-2">{t(`config.${item.key}`)}</h3>
                                            <Button size="xs" variant="outline" data-testid={`mcp-copy-${item.key}`} onClick={() => void copy(item.key, item.text)}>
                                                <Copy />{copied === item.key ? t("config.copied") : t("config.copy")}
                                            </Button>
                                        </div>
                                        <pre className="rounded-md border border-line bg-ink-2 p-2 font-mono text-xs break-all whitespace-pre-wrap">{tokenShown ? item.text : item.masked}</pre>
                                        {item.key === "claudeDesktop" && !state.proxyAvailable && <p className="text-xs text-text-2">{t("config.proxyMissing")}</p>}
                                    </section>
                                ))}
                            </DialogBody>
                        </TabsContent>

                        <TabsContent value="log" forceMount hidden={tab !== "log"} asChild>
                            <DialogBody className="space-y-2 text-sm">
                                <p className="text-xs text-text-2">{t("log.hint")}</p>
                                {calls.length === 0 ? <p className="text-text-2">{t("log.empty")}</p> : (
                                    <ul data-testid="mcp-call-log" className="divide-y divide-line">
                                        {calls.map(call => <CallRow key={call.sequence} call={call} />)}
                                    </ul>
                                )}
                                {more && <Button size="sm" variant="ghost" data-testid="mcp-log-more" onClick={() => void loadMore()}>{t("log.more")}</Button>}
                            </DialogBody>
                        </TabsContent>
                    </Tabs>
                )}
            </DialogContent>
        </Dialog>
    );
}

/** One call of the log: time, client, tool, outcome and duration; the error and the result wrap, the arguments unfold on demand. */
function CallRow({ call }: { call: McpCallResponseDto }) {
    const { t } = useTranslation("mcp");
    const [open, setOpen] = useState(false);
    // The log keeps the error code; its text comes from the errors catalog, an unknown code is shown as it is.
    const failure = call.error ? errorText(call.error, undefined, call.error) : t("log.failed");
    return (
        <li className="min-w-0 space-y-1 py-2 text-xs" data-tool={call.tool} data-sequence={call.sequence}>
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span className="text-text-3 tabular-nums">{formatDateTime(call.time)}</span>
                <span className="min-w-0 font-medium [overflow-wrap:anywhere]">{call.client}</span>
                <code className="min-w-0 font-mono break-all">{call.tool}</code>
                <Badge variant={call.succeeded ? "ok" : "danger"}>{call.succeeded ? t("log.succeeded") : t("log.failed")}</Badge>
                <span className="text-text-3 tabular-nums">{t("log.duration", { ms: formatNumber(call.durationMs) })}</span>
            </div>
            {!call.succeeded && (
                <p className="text-danger [overflow-wrap:anywhere]" data-testid="mcp-call-error">
                    {failure}
                    {call.error && failure !== call.error && <code className="ml-1 font-mono text-text-3">{call.error}</code>}
                </p>
            )}
            {call.arguments && call.arguments !== "{}" && (
                <Collapsible className="-mx-3" open={open} onOpenChange={setOpen} data-testid="mcp-call-arguments"
                    title={<span className="text-xs">{t("log.arguments")}</span>}
                    description={open ? undefined : <code className="block truncate font-mono">{call.arguments}</code>}>
                    <pre className="mx-3 rounded-md border border-line bg-ink-2 p-2 font-mono break-all whitespace-pre-wrap">{call.arguments}</pre>
                </Collapsible>
            )}
            {call.result && <p className="whitespace-pre-wrap text-text-2 [overflow-wrap:anywhere]">{call.result}</p>}
        </li>
    );
}
