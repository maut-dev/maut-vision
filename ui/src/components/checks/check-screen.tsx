import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { MonitorResponseDto, ScreenShotResponseDto } from "@/api/projects/checks/response-dto/check-source-response-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { BackgroundCropFrame } from "@/components/backgrounds/background-crop-frame";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { defaultRegion, type FramePosition } from "@/lib/backgrounds/crop-frame";
import { defaultCheckParameters, formatTime, needsRegion, parseCheckParameters, toRegion, type CheckParameterValues } from "@/lib/checks/check-view";
import { i18n } from "@/i18n";
import { errorMessage, formatNumber } from "@/lib/format";
import { useLiveCheck } from "@/lib/use-live-check";
import { CheckParameterFields, Field } from "./check-parameter-fields";
import { CheckStatus, ClassSummary, DetectionChips, DetectionOverlay, previewDetections } from "./check-results";

/** Rows of the latest frames with detections. */
const recentRows = 40;

/** "Monitor 1 — 1920 × 1080 (primary)". Call during render. */
export const monitorLabel = (monitor: MonitorResponseDto) =>
    i18n.t(monitor.isPrimary ? "checks:screen.monitorOptionPrimary" : "checks:screen.monitorOption", { index: monitor.index, width: monitor.width, height: monitor.height });

/**
 * The real-time screen check: monitors are listed before a screenshot; taking a screenshot gives a picture for the frame of the input size
 * (a screen not larger than the input is captured whole); the rate and the time limit; live counters with the actual rate, the preview,
 * what is on screen now, the latest frames with detections, Stop. Leaving the section stops the check.
 */
export function CheckScreen({ projectId, version, gpus }: { projectId: string; version: ModelVersionResponseDto; gpus: GpuInventoryResponseDto | null | undefined }) {
    const { t } = useTranslation("checks");
    const input = version.input;
    const [monitors, setMonitors] = useState<readonly MonitorResponseDto[]>();
    const [monitorIndex, setMonitorIndex] = useState<number>();
    const [shot, setShot] = useState<ScreenShotResponseDto>();
    const [position, setPosition] = useState<FramePosition>({ left: 0, top: 0 });
    const [fps, setFps] = useState("3");
    const [minutes, setMinutes] = useState("30");
    const [values, setValues] = useState<CheckParameterValues>(defaultCheckParameters);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const session = useLiveCheck(projectId, "screen", { keep: recentRows, stopOnLeave: true });

    useEffect(() => {
        let active = true;
        checksApi.listMonitors().then(list => {
            if (!active) return;
            setMonitors(list);
            setMonitorIndex(current => current ?? (list.find(item => item.isPrimary) ?? list[0])?.index);
        }, cause => { if (active) setError(errorMessage(cause, i18n.t("checks:screen.monitorsFailed"))); });
        return () => { active = false; };
    }, []);

    const monitor = monitors?.find(item => item.index === monitorIndex);
    const crop = !!monitor && needsRegion(monitor, input);
    const shotOfMonitor = shot && shot.monitor.index === monitorIndex ? shot : undefined;
    const parsed = parseCheckParameters(values);
    const fpsValue = Number(fps.replace(",", "."));
    const seconds = Math.round(Number(minutes.replace(",", ".")) * 60);
    const problem = "field" in parsed ? parsed.message
        : !(Number.isFinite(fpsValue) && fpsValue > 0 && fpsValue <= 60) ? t("screen.fpsInvalid")
            : !(Number.isSafeInteger(seconds) && seconds >= 1 && seconds <= 86_400) ? t("screen.durationInvalid")
                : undefined;
    const locked = busy || session.active;
    const check = session.check;
    const ready = !!monitor && (!crop || !!shotOfMonitor);
    const recent = [...session.journal].reverse();

    async function run(work: () => Promise<void>) {
        setBusy(true);
        setError(undefined);
        try {
            await work();
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    function capture() {
        if (monitorIndex === undefined) return;
        void run(async () => {
            const taken = await checksApi.captureScreen({ projectId, monitor: monitorIndex });
            setMonitors(taken.monitors);
            setPosition(current => shot && shot.monitor.width === taken.monitor.width && shot.monitor.height === taken.monitor.height
                ? current : defaultRegion(taken.monitor, input));
            setShot(taken);
        });
    }

    function start() {
        if (!monitor || !("parameters" in parsed) || problem) return;
        void run(async () => {
            session.attach(await checksApi.startScreen({ projectId, versionId: version.id, monitor: monitor.index, parameters: parsed.parameters, fps: fpsValue,
                maxDurationSec: seconds, region: crop ? toRegion(position, input) : null, operationId: crypto.randomUUID() }));
        });
    }

    return (
        <section className="space-y-4 rounded-lg border bg-card p-5" data-testid="check-screen">
            <div>
                <h2 className="font-medium">{t("screen.title")}</h2>
                <p className="mt-1 max-w-prose text-sm text-muted-foreground">{t("screen.text", { width: input.width, height: input.height })}</p>
            </div>
            <div className="flex flex-wrap items-end gap-3">
                <Field label={t("screen.monitor")}>
                    <Select value={monitorIndex === undefined ? "" : String(monitorIndex)} disabled={locked || !monitors?.length}
                        onValueChange={next => { if (next) setMonitorIndex(Number(next)); }}>
                        <SelectTrigger aria-label={t("screen.monitor")} className="w-72" data-testid="check-screen-monitor">
                            <SelectValue placeholder={monitors ? t("screen.noMonitors") : t("screen.findingMonitors")} />
                        </SelectTrigger>
                        <SelectContent>
                            {(monitors ?? []).map(item => <SelectItem key={item.index} value={String(item.index)}>{monitorLabel(item)}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </Field>
                <Button variant="outline" disabled={locked || monitorIndex === undefined} data-testid="check-screen-capture" onClick={capture}>
                    {shotOfMonitor ? t("screen.recapture") : t("screen.capture")}
                </Button>
            </div>
            {monitor && !crop && (
                <p className="text-sm text-muted-foreground" data-testid="check-screen-whole">
                    {t("screen.whole", { width: monitor.width, height: monitor.height })}
                </p>
            )}
            {crop && shotOfMonitor && (
                <BackgroundCropFrame
                    source={{ sourceIndex: null, timestampMs: null, name: t("screen.shotName"), width: shotOfMonitor.monitor.width, height: shotOfMonitor.monitor.height,
                        previewUrl: shotOfMonitor.url, previewWidth: shotOfMonitor.monitor.width, previewHeight: shotOfMonitor.monitor.height }}
                    frame={input}
                    value={position}
                    onChange={setPosition}
                    disabled={locked}
                    maxHeight="50vh"
                />
            )}
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
                <Field label={t("screen.fps")} htmlFor="check-screen-fps">
                    <Input id="check-screen-fps" type="number" min={0.1} max={60} step={0.5} disabled={locked} data-testid="check-screen-fps" value={fps}
                        onChange={event => setFps(event.target.value)} />
                </Field>
                <Field label={t("screen.minutes")} htmlFor="check-screen-minutes">
                    <Input id="check-screen-minutes" type="number" min={1} step={1} disabled={locked} data-testid="check-screen-minutes" value={minutes}
                        onChange={event => setMinutes(event.target.value)} />
                </Field>
                <CheckParameterFields value={values} onChange={setValues} gpus={gpus} disabled={locked} invalid={"field" in parsed ? parsed.field : undefined}
                    testPrefix="check-screen" />
            </div>
            <p role={problem ? "alert" : undefined} className={`min-h-4 text-xs ${problem ? "text-destructive" : "text-muted-foreground"}`} data-testid="check-screen-hint">
                {problem ?? (crop && !shotOfMonitor ? t("screen.captureFirst") : t("screen.fpsHint"))}
            </p>
            {error && <Alert variant="destructive" data-testid="check-screen-error-notice"><AlertDescription>{error}</AlertDescription></Alert>}
            <div className="flex flex-wrap gap-2">
                <Button disabled={!ready || !!problem || locked} data-testid="check-screen-start" onClick={start}>{t("screen.start")}</Button>
                {session.active && (
                    <Button variant="outline" disabled={check?.state === "cancelling"} data-testid="check-screen-stop" onClick={() => void run(session.stop)}>{t("stop")}</Button>
                )}
            </div>
            {check && (
                <div className="space-y-4 border-t pt-4" data-testid="check-screen-result" data-check-id={check.id}>
                    <CheckStatus check={check} testPrefix="check-screen" />
                    <p className="text-xs tabular-nums text-muted-foreground" data-testid="check-screen-stats">
                        {[
                            t("screen.stats", { frames: formatNumber(check.progress.framesProcessed), detected: formatNumber(check.summary.framesWithDetections) }),
                            ...(check.progress.fps !== null ? [t("screen.statsFps", { fps: formatNumber(check.progress.fps, { maximumFractionDigits: 1 }) })] : []),
                            t("screen.statsElapsed", { time: formatTime(check.progress.elapsedMs) }),
                        ].join(" · ")}
                    </p>
                    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
                        {check.preview ? (
                            <div className="space-y-1">
                                <DetectionOverlay src={check.preview.url} width={check.preview.width} height={check.preview.height}
                                    detections={previewDetections(check, session.journal)} alt={t("screen.previewAlt")} testId="check-screen-preview" />
                                <p className="text-center text-xs text-muted-foreground">{t("screen.previewNote", { time: formatTime(check.preview.timeMs) })}</p>
                            </div>
                        ) : (
                            <p className="text-sm text-muted-foreground">{session.active ? t("screen.waiting") : t("screen.noFrames")}</p>
                        )}
                        <div className="space-y-2" data-testid="check-screen-now">
                            <h3 className="text-sm font-medium">{t("screen.now")}</h3>
                            {check.lastFrame ? <DetectionChips detections={check.lastFrame.detections} /> : <p className="text-sm text-muted-foreground">—</p>}
                        </div>
                    </div>
                    <div className="grid gap-4 lg:grid-cols-2">
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">{t("classSummary")}</h3>
                            <ClassSummary check={check} testId="check-screen-classes" />
                        </div>
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">{t("screen.recent")}</h3>
                            {recent.length ? (
                                <div className="max-h-80 overflow-y-auto rounded-md border">
                                    <table className="w-full text-sm" data-testid="check-screen-journal">
                                        <tbody>
                                            {recent.map(item => (
                                                <tr key={item.ordinal} className="border-b align-top last:border-b-0" data-ordinal={item.ordinal}>
                                                    <td className="w-20 px-3 py-1.5 tabular-nums text-muted-foreground">{formatTime(item.timeMs)}</td>
                                                    <td className="px-3 py-1.5"><DetectionChips detections={item.detections} /></td>
                                                </tr>
                                            ))}
                                        </tbody>
                                    </table>
                                </div>
                            ) : (
                                <p className="text-sm text-muted-foreground">{t("screen.nothingYet")}</p>
                            )}
                        </div>
                    </div>
                </div>
            )}
        </section>
    );
}
