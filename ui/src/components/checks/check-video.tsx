import { useState } from "react";
import { useTranslation } from "react-i18next";
import { checksApi } from "@/api/projects/checks/checks-api";
import type { CheckVideoFrameResponseDto, CheckVideoSelectionResponseDto } from "@/api/projects/checks/response-dto/check-source-response-dto";
import type { GpuInventoryResponseDto } from "@/api/projects/training/response-dto/training-runtime-response-dto";
import type { ModelVersionResponseDto } from "@/api/projects/training/response-dto/model-version-response-dto";
import { BackgroundCropFrame } from "@/components/backgrounds/background-crop-frame";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { defaultRegion, type FramePosition } from "@/lib/backgrounds/crop-frame";
import {
    defaultCheckParameters, formatTime, needsRegion, parseCheckParameters, parseStep, stepEstimate, toRegion, type CheckParameterValues,
} from "@/lib/checks/check-view";
import { errorMessage, formatNumber } from "@/lib/format";
import { useLiveCheck } from "@/lib/use-live-check";
import { CheckParameterFields, Field } from "./check-parameter-fields";
import { CheckStatus, ClassSummary, DetectionChips, DetectionOverlay, previewDetections } from "./check-results";

/** Journal rows kept in memory and shown per page. */
const journalKeep = 1000;
const journalPageRows = 100;

type VideoSelection = CheckVideoSelectionResponseDto & { token: string; width: number; height: number; durationMs: number; frame: CheckVideoFrameResponseDto };

/**
 * The video check: a sample frame from the middle and Show another frame, a frame of the input size (a video not larger than the input is
 * checked whole), the step with the estimated frame count, progress, the preview and the journal of detections (only frames with
 * detections, newest first).
 */
export function CheckVideo({ projectId, version, gpus }: { projectId: string; version: ModelVersionResponseDto; gpus: GpuInventoryResponseDto | null | undefined }) {
    const { t } = useTranslation("checks");
    const input = version.input;
    const [video, setVideo] = useState<VideoSelection>();
    const [frame, setFrame] = useState<CheckVideoFrameResponseDto>();
    const [position, setPosition] = useState<FramePosition>({ left: 0, top: 0 });
    const [step, setStep] = useState("1");
    const [values, setValues] = useState<CheckParameterValues>(defaultCheckParameters);
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const [shown, setShown] = useState(journalPageRows);
    const session = useLiveCheck(projectId, "video", { keep: journalKeep });

    const parsed = parseCheckParameters(values);
    const stepMs = parseStep(step);
    const problem = "field" in parsed ? parsed.message : stepMs === null ? t("video.stepInvalid") : undefined;
    const crop = !!video && needsRegion(video, input);
    const locked = busy || session.active;
    const check = session.check;
    const progress = check?.progress;
    const share = check?.state === "succeeded" ? 1 : progress?.durationMs ? Math.min(1, (progress.positionMs ?? 0) / progress.durationMs) : 0;
    const rows = [...session.journal].reverse();

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

    function pick() {
        void run(async () => {
            const selected = await checksApi.selectVideo({ projectId });
            if (!selected.token || !selected.frame || selected.width === null || selected.height === null || selected.durationMs === null) return;
            const next = selected as VideoSelection;
            setVideo(next);
            setFrame(next.frame);
            setPosition(current => video && video.width === next.width && video.height === next.height ? current : defaultRegion(next, input));
        });
    }

    function anotherFrame() {
        if (!video) return;
        void run(async () => {
            const timestampMs = Math.floor(Math.random() * Math.max(1, video.durationMs - 1));
            setFrame(await checksApi.previewVideoFrame({ projectId, selectionToken: video.token, timestampMs }));
        });
    }

    function start() {
        if (!video || stepMs === null || !("parameters" in parsed)) return;
        void run(async () => {
            setShown(journalPageRows);
            session.attach(await checksApi.startVideo({ projectId, versionId: version.id, selectionToken: video.token, parameters: parsed.parameters, stepMs,
                region: crop ? toRegion(position, input) : null, operationId: crypto.randomUUID() }));
        });
    }

    return (
        <section className="space-y-4 rounded-lg border bg-card p-5" data-testid="check-video">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div>
                    <h2 className="font-medium">{t("video.title")}</h2>
                    <p className="mt-1 max-w-prose text-sm text-muted-foreground">{t("video.text", { width: input.width, height: input.height })}</p>
                </div>
                <Button variant="outline" disabled={locked} data-testid="check-video-pick" onClick={pick}>{video ? t("video.other") : t("video.pick")}</Button>
            </div>
            <p className="min-h-5 truncate text-xs text-muted-foreground" data-testid="check-video-source">
                {video
                    ? `${video.name} · ${video.width} × ${video.height} · ${formatTime(video.durationMs)}`
                    : t("video.prompt")}
            </p>
            {video && frame && (
                <div className="space-y-2">
                    {crop ? (
                        <BackgroundCropFrame
                            source={{ sourceIndex: null, timestampMs: frame.timestampMs, name: video.name ?? t("video.name"), width: frame.width, height: frame.height,
                                previewUrl: frame.url, previewWidth: frame.previewWidth, previewHeight: frame.previewHeight }}
                            frame={input}
                            value={position}
                            onChange={setPosition}
                            disabled={locked}
                            maxHeight="50vh"
                        />
                    ) : (
                        <>
                            <img src={frame.url} alt={video.name ?? t("video.frameAlt")} className="mx-auto max-h-[50vh] rounded-md" draggable={false} data-testid="check-video-frame" />
                            <p className="text-center text-xs text-muted-foreground" data-testid="check-video-whole">{t("video.whole")}</p>
                        </>
                    )}
                    <div className="flex items-center justify-center gap-3">
                        <span className="text-xs tabular-nums text-muted-foreground" data-testid="check-video-frame-time">{t("video.frame", { time: formatTime(frame.timestampMs) })}</span>
                        <Button variant="ghost" size="sm" disabled={locked} data-testid="check-video-another" onClick={anotherFrame}>{t("video.another")}</Button>
                    </div>
                </div>
            )}
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                <Field label={t("video.step")} htmlFor="check-video-step">
                    <Input id="check-video-step" type="number" min={0.001} step="any" disabled={locked} aria-invalid={stepMs === null || undefined}
                        data-testid="check-video-step" value={step} onChange={event => setStep(event.target.value)} />
                </Field>
                <CheckParameterFields value={values} onChange={setValues} gpus={gpus} disabled={locked} invalid={"field" in parsed ? parsed.field : undefined}
                    testPrefix="check-video" />
            </div>
            <p role={problem ? "alert" : undefined} className={`min-h-4 text-xs ${problem ? "text-destructive" : "text-muted-foreground"}`} data-testid="check-video-hint">
                {problem ?? (video && stepMs !== null ? stepEstimate(video.durationMs, stepMs) : "")}
            </p>
            {error && <Alert variant="destructive" data-testid="check-video-error-notice"><AlertDescription>{error}</AlertDescription></Alert>}
            <div className="flex flex-wrap gap-2">
                <Button disabled={!video || !!problem || locked} data-testid="check-video-start" onClick={start}>{t("video.start")}</Button>
                {session.active && (
                    <Button variant="outline" disabled={check?.state === "cancelling"} data-testid="check-video-stop" onClick={() => void run(session.stop)}>{t("stop")}</Button>
                )}
            </div>
            {check && progress && (
                <div className="space-y-4 border-t pt-4" data-testid="check-video-result" data-check-id={check.id}>
                    <CheckStatus check={check} testPrefix="check-video" />
                    <div className="space-y-1">
                        <div className="h-2 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label={t("video.progressAria")} aria-valuemin={0} aria-valuemax={100}
                            aria-valuenow={Math.round(share * 100)}>
                            <div className="h-full bg-brand transition-[width]" style={{ width: `${share * 100}%` }} />
                        </div>
                        <p className="text-xs tabular-nums text-muted-foreground" data-testid="check-video-progress">
                            {t(progress.framesTotal ? "video.progressOfTotal" : "video.progress", {
                                position: formatTime(progress.positionMs),
                                duration: formatTime(progress.durationMs ?? check.source.durationMs),
                                frames: formatNumber(progress.framesProcessed),
                                total: formatNumber(progress.framesTotal ?? 0),
                            })}
                            {" · "}{t("video.detected", { detected: formatNumber(check.summary.framesWithDetections) })}
                        </p>
                    </div>
                    <div className="grid gap-4 lg:grid-cols-2">
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">{t("classSummary")}</h3>
                            <ClassSummary check={check} testId="check-video-classes" />
                        </div>
                        {check.preview && (
                            <div className="space-y-2">
                                <h3 className="text-sm font-medium">{t("video.frame", { time: formatTime(check.preview.timeMs) })}</h3>
                                <DetectionOverlay src={check.preview.url} width={check.preview.width} height={check.preview.height}
                                    detections={previewDetections(check, session.journal)} alt={t("video.frameAltAt", { time: formatTime(check.preview.timeMs) })}
                                    testId="check-video-preview" />
                            </div>
                        )}
                    </div>
                    <div className="space-y-2">
                        <h3 className="text-sm font-medium">{t("video.journal")}</h3>
                        {rows.length ? (
                            <div className="max-h-[28rem] overflow-y-auto rounded-md border">
                                <table className="w-full text-sm" data-testid="check-video-journal">
                                    <thead className="sticky top-0 bg-card">
                                        <tr className="border-b text-left text-xs text-muted-foreground">
                                            <th className="w-24 px-3 py-2 font-normal">{t("video.time")}</th>
                                            <th className="w-20 px-3 py-2 text-right font-normal">{t("video.frameColumn")}</th>
                                            <th className="px-3 py-2 font-normal">{t("video.classes")}</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {rows.slice(0, shown).map(item => (
                                            <tr key={item.ordinal} className="border-b align-top last:border-b-0" data-ordinal={item.ordinal}>
                                                <td className="px-3 py-1.5 tabular-nums">{formatTime(item.timeMs)}</td>
                                                <td className="px-3 py-1.5 text-right tabular-nums text-muted-foreground">{item.index}</td>
                                                <td className="px-3 py-1.5"><DetectionChips detections={item.detections} /></td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        ) : (
                            <p className="text-sm text-muted-foreground">{t("video.noDetections")}</p>
                        )}
                        <div className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
                            <span data-testid="check-video-journal-count">
                                {t("video.shown", { shown: formatNumber(Math.min(shown, rows.length)), kept: formatNumber(check.journal.kept) })}
                            </span>
                            {rows.length > shown && <Button variant="ghost" size="xs" onClick={() => setShown(value => value + journalPageRows)}>{t("video.more")}</Button>}
                            {rows.length <= shown && (session.journal[0]?.ordinal ?? 0) > check.journal.firstOrdinal && (
                                <Button variant="ghost" size="xs" disabled={session.loadingOlder} data-testid="check-video-older"
                                    onClick={() => { setShown(value => value + journalPageRows); void session.olderJournal(); }}>{t("video.older")}</Button>
                            )}
                        </div>
                        {check.journal.truncated && (
                            <p className="text-xs text-muted-foreground" data-testid="check-video-truncated">
                                {t("video.truncated", { kept: formatNumber(check.journal.kept), total: formatNumber(check.journal.total) })}
                            </p>
                        )}
                    </div>
                </div>
            )}
        </section>
    );
}
