import { useEffect, useState } from "react";
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
import { errorMessage } from "@/lib/format";
import { useLiveCheck } from "@/lib/use-live-check";
import { CheckParameterFields, Field } from "./check-parameter-fields";
import { CheckStatus, ClassSummary, DetectionChips, DetectionOverlay, previewDetections } from "./check-results";

/** Строк «Последние кадры с обнаружениями». */
const recentRows = 40;

export const monitorLabel = (monitor: MonitorResponseDto) =>
    `Монитор ${monitor.index} — ${monitor.width} × ${monitor.height}${monitor.isPrimary ? " (основной)" : ""}`;

/**
 * «Проверка с экрана в реальном времени»: мониторы доступны до снимка; «Снять экран» даёт снимок для рамки размера входа (экран не
 * больше входа — захватывается целиком); частота и предел времени; живые счётчики с фактической частотой, превью, «Сейчас на
 * экране», последние кадры с обнаружениями, «Остановить». Уход из раздела останавливает проверку.
 */
export function CheckScreen({ projectId, version, gpus }: { projectId: string; version: ModelVersionResponseDto; gpus: GpuInventoryResponseDto | null | undefined }) {
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
        }, cause => { if (active) setError(errorMessage(cause, "Не удалось получить список мониторов.")); });
        return () => { active = false; };
    }, []);

    const monitor = monitors?.find(item => item.index === monitorIndex);
    const crop = !!monitor && needsRegion(monitor, input);
    const shotOfMonitor = shot && shot.monitor.index === monitorIndex ? shot : undefined;
    const parsed = parseCheckParameters(values);
    const fpsValue = Number(fps.replace(",", "."));
    const seconds = Math.round(Number(minutes.replace(",", ".")) * 60);
    const problem = "field" in parsed ? parsed.message
        : !(Number.isFinite(fpsValue) && fpsValue > 0 && fpsValue <= 60) ? "Кадров в секунду — число больше 0 и не больше 60."
            : !(Number.isSafeInteger(seconds) && seconds >= 1 && seconds <= 86_400) ? "«Остановить через» — от 1 секунды до 24 часов (в минутах)."
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
                <h2 className="font-medium">Проверка с экрана в реальном времени</h2>
                <p className="mt-1 max-w-prose text-sm text-muted-foreground">
                    Снимите экран и поставьте рамку {input.width} × {input.height} px туда, где появляются объекты. Рамка захватывается с заданной частотой
                    и прогоняется через модель, пока проверку не остановят или не выйдет время. Игра должна быть в оконном режиме без рамок: эксклюзивный
                    полноэкранный режим Windows не отдаёт.
                </p>
            </div>
            <div className="flex flex-wrap items-end gap-3">
                <Field label="Монитор">
                    <Select value={monitorIndex === undefined ? "" : String(monitorIndex)} disabled={locked || !monitors?.length}
                        onValueChange={next => { if (next) setMonitorIndex(Number(next)); }}>
                        <SelectTrigger aria-label="Монитор" className="w-72" data-testid="check-screen-monitor">
                            <SelectValue placeholder={monitors ? "Мониторов нет" : "Поиск мониторов…"} />
                        </SelectTrigger>
                        <SelectContent>
                            {(monitors ?? []).map(item => <SelectItem key={item.index} value={String(item.index)}>{monitorLabel(item)}</SelectItem>)}
                        </SelectContent>
                    </Select>
                </Field>
                <Button variant="outline" disabled={locked || monitorIndex === undefined} data-testid="check-screen-capture" onClick={capture}>
                    {shotOfMonitor ? "Снять экран заново" : "Снять экран"}
                </Button>
            </div>
            {monitor && !crop && (
                <p className="text-sm text-muted-foreground" data-testid="check-screen-whole">
                    Экран {monitor.width} × {monitor.height} не больше входа модели: захватывается целиком.
                </p>
            )}
            {crop && shotOfMonitor && (
                <BackgroundCropFrame
                    source={{ sourceIndex: null, timestampMs: null, name: "Снимок экрана", width: shotOfMonitor.monitor.width, height: shotOfMonitor.monitor.height,
                        previewUrl: shotOfMonitor.url, previewWidth: shotOfMonitor.monitor.width, previewHeight: shotOfMonitor.monitor.height }}
                    frame={input}
                    value={position}
                    onChange={setPosition}
                    disabled={locked}
                    maxHeight="50vh"
                />
            )}
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
                <Field label="Кадров в секунду" htmlFor="check-screen-fps">
                    <Input id="check-screen-fps" type="number" min={0.1} max={60} step={0.5} disabled={locked} data-testid="check-screen-fps" value={fps}
                        onChange={event => setFps(event.target.value)} />
                </Field>
                <Field label="Остановить через, мин" htmlFor="check-screen-minutes">
                    <Input id="check-screen-minutes" type="number" min={1} step={1} disabled={locked} data-testid="check-screen-minutes" value={minutes}
                        onChange={event => setMinutes(event.target.value)} />
                </Field>
                <CheckParameterFields value={values} onChange={setValues} gpus={gpus} disabled={locked} invalid={"field" in parsed ? parsed.field : undefined}
                    testPrefix="check-screen" />
            </div>
            <p role={problem ? "alert" : undefined} className={`min-h-4 text-xs ${problem ? "text-destructive" : "text-muted-foreground"}`} data-testid="check-screen-hint">
                {problem ?? (crop && !shotOfMonitor ? "Сначала снимите экран, чтобы поставить рамку." : "2–5 кадров в секунду обычно достаточно; выше — больше нагрузка на видеокарту.")}
            </p>
            {error && <Alert variant="destructive" data-testid="check-screen-error-notice"><AlertDescription>{error}</AlertDescription></Alert>}
            <div className="flex flex-wrap gap-2">
                <Button disabled={!ready || !!problem || locked} data-testid="check-screen-start" onClick={start}>Запустить проверку с экрана</Button>
                {session.active && (
                    <Button variant="outline" disabled={check?.state === "cancelling"} data-testid="check-screen-stop" onClick={() => void run(session.stop)}>Остановить</Button>
                )}
            </div>
            {check && (
                <div className="space-y-4 border-t pt-4" data-testid="check-screen-result" data-check-id={check.id}>
                    <CheckStatus check={check} testPrefix="check-screen" />
                    <p className="text-xs tabular-nums text-muted-foreground" data-testid="check-screen-stats">
                        Кадров {check.progress.framesProcessed.toLocaleString("ru-RU")} · с обнаружениями {check.summary.framesWithDetections.toLocaleString("ru-RU")}
                        {check.progress.fps !== null ? ` · ${check.progress.fps.toLocaleString("ru-RU", { maximumFractionDigits: 1 })} к/с` : ""}
                        {` · ${formatTime(check.progress.elapsedMs)} от начала`}
                    </p>
                    <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_20rem]">
                        {check.preview ? (
                            <div className="space-y-1">
                                <DetectionOverlay src={check.preview.url} width={check.preview.width} height={check.preview.height}
                                    detections={previewDetections(check, session.journal)} alt="Последний кадр с экрана" testId="check-screen-preview" />
                                <p className="text-center text-xs text-muted-foreground">Кадр {formatTime(check.preview.timeMs)} — картинка обновляется раз в секунду.</p>
                            </div>
                        ) : (
                            <p className="text-sm text-muted-foreground">{session.active ? "Ждём первый кадр…" : "Кадров нет."}</p>
                        )}
                        <div className="space-y-2" data-testid="check-screen-now">
                            <h3 className="text-sm font-medium">Сейчас на экране</h3>
                            {check.lastFrame ? <DetectionChips detections={check.lastFrame.detections} /> : <p className="text-sm text-muted-foreground">—</p>}
                        </div>
                    </div>
                    <div className="grid gap-4 lg:grid-cols-2">
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">Итог по классам</h3>
                            <ClassSummary check={check} testId="check-screen-classes" />
                        </div>
                        <div className="space-y-2">
                            <h3 className="text-sm font-medium">Последние кадры с обнаружениями</h3>
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
                                <p className="text-sm text-muted-foreground">Пока ничего не обнаружено.</p>
                            )}
                        </div>
                    </div>
                </div>
            )}
        </section>
    );
}
