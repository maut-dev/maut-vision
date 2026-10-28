import { useState } from "react";
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
import { errorMessage, locale } from "@/lib/format";
import { useLiveCheck } from "@/lib/use-live-check";
import { CheckParameterFields, Field } from "./check-parameter-fields";
import { CheckStatus, ClassSummary, DetectionChips, DetectionOverlay, previewDetections } from "./check-results";

/** Строк журнала в памяти и на странице. */
const journalKeep = 1000;
const journalPageRows = 100;

type VideoSelection = CheckVideoSelectionResponseDto & { token: string; width: number; height: number; durationMs: number; frame: CheckVideoFrameResponseDto };

/**
 * «Проверка по видео»: кадр-пример из середины и «Показать другой кадр», рамка размера входа (видео не больше входа — кадр
 * целиком), «Шаг, с» с оценкой числа кадров, ход, превью и журнал «Где и что обнаружено» (только кадры с обнаружениями, новые сверху).
 */
export function CheckVideo({ projectId, version, gpus }: { projectId: string; version: ModelVersionResponseDto; gpus: GpuInventoryResponseDto | null | undefined }) {
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
    const problem = "field" in parsed ? parsed.message : stepMs === null ? "Шаг — положительное число секунд (не меньше 1 мс)." : undefined;
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
                    <h2 className="font-medium">Проверка по видео</h2>
                    <p className="mt-1 max-w-prose text-sm text-muted-foreground">
                        Видео MP4, MKV, AVI, MOV, WebM или M4V. Кадры берутся с заданным шагом подряд, без перемотки. Если видео больше входа модели, из каждого
                        кадра вырезается рамка {input.width} × {input.height} px — модель обучена ровно на таких кадрах.
                    </p>
                </div>
                <Button variant="outline" disabled={locked} data-testid="check-video-pick" onClick={pick}>{video ? "Другое видео" : "Выбрать видео"}</Button>
            </div>
            <p className="min-h-5 truncate text-xs text-muted-foreground" data-testid="check-video-source">
                {video
                    ? `${video.name} · ${video.width} × ${video.height} · ${formatTime(video.durationMs)}`
                    : "Выберите видео — появится кадр-пример для рамки."}
            </p>
            {video && frame && (
                <div className="space-y-2">
                    {crop ? (
                        <BackgroundCropFrame
                            source={{ sourceIndex: null, timestampMs: frame.timestampMs, name: video.name ?? "Видео", width: frame.width, height: frame.height,
                                previewUrl: frame.url, previewWidth: frame.previewWidth, previewHeight: frame.previewHeight }}
                            frame={input}
                            value={position}
                            onChange={setPosition}
                            disabled={locked}
                            maxHeight="50vh"
                        />
                    ) : (
                        <>
                            <img src={frame.url} alt={video.name ?? "Кадр видео"} className="mx-auto max-h-[50vh] rounded-md" draggable={false} data-testid="check-video-frame" />
                            <p className="text-center text-xs text-muted-foreground" data-testid="check-video-whole">Видео не больше входа модели: проверяется кадр целиком.</p>
                        </>
                    )}
                    <div className="flex items-center justify-center gap-3">
                        <span className="text-xs tabular-nums text-muted-foreground" data-testid="check-video-frame-time">Кадр {formatTime(frame.timestampMs)}</span>
                        <Button variant="ghost" size="sm" disabled={locked} data-testid="check-video-another" onClick={anotherFrame}>Показать другой кадр</Button>
                    </div>
                </div>
            )}
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
                <Field label="Шаг, с" htmlFor="check-video-step">
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
                <Button disabled={!video || !!problem || locked} data-testid="check-video-start" onClick={start}>Проверить видео</Button>
                {session.active && (
                    <Button variant="outline" disabled={check?.state === "cancelling"} data-testid="check-video-stop" onClick={() => void run(session.stop)}>Остановить</Button>
                )}
            </div>
            {check && progress && (
                <div className="space-y-4 border-t pt-4" data-testid="check-video-result" data-check-id={check.id}>
                    <CheckStatus check={check} testPrefix="check-video" />
                    <div className="space-y-1">
                        <div className="h-2 overflow-hidden rounded-full bg-secondary" role="progressbar" aria-label="Ход проверки видео" aria-valuemin={0} aria-valuemax={100}
                            aria-valuenow={Math.round(share * 100)}>
                            <div className="h-full bg-brand transition-[width]" style={{ width: `${share * 100}%` }} />
                        </div>
                        <p className="text-xs tabular-nums text-muted-foreground" data-testid="check-video-progress">
                            {formatTime(progress.positionMs)} из {formatTime(progress.durationMs ?? check.source.durationMs)} · проверено кадров {progress.framesProcessed.toLocaleString(locale())}
                            {progress.framesTotal ? ` из ~${progress.framesTotal.toLocaleString(locale())}` : ""} · с обнаружениями {check.summary.framesWithDetections.toLocaleString(locale())}
                        </p>
                    </div>
                    <div className="grid gap-4 lg:grid-cols-2">
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">Итог по классам</h3>
                            <ClassSummary check={check} testId="check-video-classes" />
                        </div>
                        {check.preview && (
                            <div className="space-y-2">
                                <h3 className="text-sm font-medium">Кадр {formatTime(check.preview.timeMs)}</h3>
                                <DetectionOverlay src={check.preview.url} width={check.preview.width} height={check.preview.height}
                                    detections={previewDetections(check, session.journal)} alt={`Кадр видео ${formatTime(check.preview.timeMs)}`} testId="check-video-preview" />
                            </div>
                        )}
                    </div>
                    <div className="space-y-2">
                        <h3 className="text-sm font-medium">Где и что обнаружено</h3>
                        {rows.length ? (
                            <div className="max-h-[28rem] overflow-y-auto rounded-md border">
                                <table className="w-full text-sm" data-testid="check-video-journal">
                                    <thead className="sticky top-0 bg-card">
                                        <tr className="border-b text-left text-xs text-muted-foreground">
                                            <th className="w-24 px-3 py-2 font-normal">Время</th>
                                            <th className="w-20 px-3 py-2 text-right font-normal">Кадр</th>
                                            <th className="px-3 py-2 font-normal">Классы и уверенность</th>
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
                            <p className="text-sm text-muted-foreground">Кадров с обнаружениями пока нет.</p>
                        )}
                        <div className="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
                            <span data-testid="check-video-journal-count">Показано {Math.min(shown, rows.length).toLocaleString(locale())} из {check.journal.kept.toLocaleString(locale())}</span>
                            {rows.length > shown && <Button variant="ghost" size="xs" onClick={() => setShown(value => value + journalPageRows)}>Показать ещё</Button>}
                            {rows.length <= shown && (session.journal[0]?.ordinal ?? 0) > check.journal.firstOrdinal && (
                                <Button variant="ghost" size="xs" disabled={session.loadingOlder} data-testid="check-video-older"
                                    onClick={() => { setShown(value => value + journalPageRows); void session.olderJournal(); }}>Показать более ранние</Button>
                            )}
                        </div>
                        {check.journal.truncated && (
                            <p className="text-xs text-muted-foreground" data-testid="check-video-truncated">
                                Журнал хранит первые {check.journal.kept.toLocaleString(locale())} кадров с обнаружениями из {check.journal.total.toLocaleString(locale())};
                                итог по классам — по всем кадрам.
                            </p>
                        )}
                    </div>
                </div>
            )}
        </section>
    );
}
