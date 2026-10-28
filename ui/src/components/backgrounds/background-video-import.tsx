import { useEffect, useRef, useState } from "react";
import { backgroundsWord } from "@/api/projects/backgrounds/background-import-rules";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundImportPreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-import-preview-response-dto";
import type { BackgroundSourcePreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { isSelectedVideo, type SelectedVideoDto } from "@/api/projects/images/response-dto/video-selection-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { clampRegion, defaultRegion, type FramePosition } from "@/lib/backgrounds/crop-frame";
import { defaultVideoFields, videoFormMessage, videoFramesAvailable, videoRangeOf, type VideoFields } from "@/lib/backgrounds/video-form";
import { errorMessage } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { Field } from "./background-blur-settings";
import { BackgroundCropFrame } from "./background-crop-frame";
import { BackgroundImportSamples } from "./background-import-samples";

/**
 * «Нарезать из видео»: одна рамка размера набора на всё видео. Видео выбирается в нативном окне (без хеширования), кадр-
 * пример — середина видео, «Показать другой кадр» — случайная отметка. Поля проверяются теми же правилами, что у host;
 * примеры и запуск — с одним operationId. Выбор действует на один запуск: после него видео выбирается снова.
 */
export function BackgroundVideoImport({ projectId, set, blur, disabled, start }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    blur: BackgroundBlurState;
    disabled: boolean;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
}) {
    const frame = set.targetSize;
    const [video, setVideo] = useState<SelectedVideoDto>();
    const [example, setExample] = useState<BackgroundSourcePreviewResponseDto>();
    const [region, setRegion] = useState<FramePosition>({ left: 0, top: 0 });
    const [fields, setFields] = useState<VideoFields>(defaultVideoFields);
    const [preview, setPreview] = useState<BackgroundImportPreviewResponseDto>();
    const [operationId, setOperationId] = useState(() => crypto.randomUUID());
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const [started, setStarted] = useState(false);
    const alive = useRef(true);
    /** Показанный кадр-пример: другой кадр того же размера сохраняет рамку. */
    const shown = useRef<BackgroundSourcePreviewResponseDto | undefined>(undefined);

    useEffect(() => {
        alive.current = true;
        return () => { alive.current = false; };
    }, []);

    const range = videoRangeOf(fields);
    const message = video ? videoFormMessage(video, frame, region, fields) : null;
    const available = video ? videoFramesAvailable(video, range) : 0;
    const ready = !!video && !!example && !message && blur.valid;
    const locked = disabled || busy;
    const paramsKey = JSON.stringify([video?.selectionToken, region, range, fields.skipSimilar, blur.key, operationId]);

    // Смена параметров делает примеры устаревшими.
    useEffect(() => setPreview(undefined), [paramsKey]);

    async function act(action: () => Promise<void>) {
        setBusy(true);
        setError(undefined);
        try {
            await action();
        } catch (cause) {
            if (alive.current) setError(errorMessage(cause));
        } finally {
            if (alive.current) setBusy(false);
        }
    }

    async function showExample(selected: SelectedVideoDto, timestampMs: number) {
        const loaded = await backgroundsApi.previewVideoFrame({ projectId, selectionToken: selected.selectionToken, timestampMs });
        if (!alive.current) return;
        const current = shown.current;
        shown.current = loaded;
        setExample(loaded);
        setRegion(position => current && current.width === loaded.width && current.height === loaded.height
            ? clampRegion(position, loaded, frame)
            : defaultRegion(loaded, frame));
    }

    function choose() {
        void act(async () => {
            const selected = await backgroundsApi.selectVideo();
            if (!isSelectedVideo(selected) || !alive.current) return;
            setVideo(selected);
            setExample(undefined);
            shown.current = undefined;
            setFields(current => ({ ...current, start: defaultVideoFields.start }));
            setOperationId(crypto.randomUUID());
            setStarted(false);
            if (selected.width >= frame.width && selected.height >= frame.height) await showExample(selected, Math.floor(selected.durationMs / 2));
        });
    }

    function request(): ImportBackgroundsRequestDto {
        return {
            projectId,
            setId: set.id,
            selectionToken: video!.selectionToken,
            operationId,
            region,
            startMs: range.startMs,
            intervalMs: range.intervalMs,
            count: range.count,
            skipSimilar: fields.skipSimilar,
            blur: blur.value,
        };
    }

    function showSamples() {
        if (!ready) return;
        const sent = request();
        void act(async () => {
            const result = await backgroundsApi.previewImport(sent);
            if (alive.current) setPreview(result);
        });
    }

    function launch() {
        if (!ready) return;
        const sent = request();
        void act(async () => {
            await start(sent);
            if (!alive.current) return;
            // Выбор привязан к запуску: следующий запуск — новое видео и новая операция.
            setVideo(undefined);
            setExample(undefined);
            setPreview(undefined);
            setOperationId(crypto.randomUUID());
            setStarted(true);
        });
    }

    const field = (name: "count" | "interval" | "start", label: string, props: { min: number; step: number | "any" }) => (
        <Field label={label}>
            <Input type="number" disabled={!video || locked} data-testid={`bg-video-${name}`} value={fields[name]}
                onChange={event => setFields(current => ({ ...current, [name]: event.target.value }))} {...props} />
        </Field>
    );
    const countValid = Number.isInteger(range.count) && range.count > 0;

    return (
        <section className="space-y-4" aria-label="Нарезка из видео" data-testid="bg-video-import">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                    <h4 className="text-sm font-medium">Нарезка из видео</h4>
                    <p className="text-xs text-muted-foreground">
                        Одна рамка {frame.width} × {frame.height} px на всё видео: из каждого взятого кадра вырезается этот участок.
                    </p>
                </div>
                <Button variant="outline" size="sm" disabled={locked} data-testid="bg-video-choose" onClick={choose}>{video ? "Другое видео" : "Выбрать видео"}</Button>
            </div>
            <p className="min-h-5 truncate text-xs text-muted-foreground" data-testid="bg-video-info">
                {video
                    ? `${video.displayName} · ${video.width} × ${video.height} · ${(video.durationMs / 1000).toFixed(1)} с`
                    : started
                        ? "Задача запущена — ход и итог в истории подготовки. Для следующего запуска выберите видео снова."
                        : "Выберите видео — появится кадр-пример, на котором ставится рамка."}
            </p>
            {video && example && (
                <div className="space-y-2">
                    <BackgroundCropFrame source={example} frame={frame} value={region} onChange={setRegion} disabled={locked} maxHeight="40vh" />
                    <div className="flex justify-center">
                        <Button variant="ghost" size="sm" disabled={locked} data-testid="bg-video-another"
                            onClick={() => void act(() => showExample(video, Math.floor(Math.random() * Math.max(1, video.durationMs - 1))))}>
                            Показать другой кадр
                        </Button>
                    </div>
                </div>
            )}
            <div className="grid gap-3 sm:grid-cols-2">
                {field("count", "Сколько фонов нарезать", { min: 1, step: 1 })}
                {field("interval", "Брать кадр раз в, с", { min: 0.001, step: "any" })}
            </div>
            <label className="flex items-start gap-2 text-sm">
                <Checkbox className="mt-0.5" disabled={!video || locked} checked={fields.skipSimilar} data-testid="bg-video-skip-similar"
                    onCheckedChange={checked => setFields(current => ({ ...current, skipSimilar: checked === true }))} />
                <span>
                    Пропускать повторяющиеся кадры
                    <span className="block text-xs text-muted-foreground">
                        Кадр, почти не отличающийся от уже взятого (стоящая камера, пауза), не считается — вместо него берётся следующий. Если разных
                        кадров мало, фонов получится меньше заданного.
                    </span>
                </span>
            </label>
            <details className="rounded-md border px-3 py-2">
                <summary className="cursor-pointer text-sm text-muted-foreground select-none">Дополнительно</summary>
                <div className="mt-3 grid gap-3 sm:grid-cols-[12rem_minmax(0,1fr)] sm:items-end">
                    {field("start", "Начать с, с", { min: 0, step: "any" })}
                    <p className="pb-2 text-xs text-muted-foreground">Полезно, чтобы не брать заставку или меню в начале видео.</p>
                </div>
            </details>
            <p role={message ? "alert" : undefined} className={`min-h-5 text-xs ${message ? "text-destructive" : "text-muted-foreground"}`} data-testid="bg-video-message">
                {message ?? (video && available ? `С таким шагом доступно до ${available} кадров.` : "")}
            </p>
            {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
            {busy && !preview && video && <p role="status" className="text-xs text-muted-foreground">Готовим кадры…</p>}
            <BackgroundImportSamples preview={preview} />
            <div className="flex flex-wrap gap-2">
                <Button variant="outline" disabled={!ready || locked} data-testid="bg-video-samples" onClick={showSamples}>Показать примеры</Button>
                <Button disabled={!ready || locked} data-testid="bg-video-run" onClick={launch}>
                    Нарезать {countValid ? range.count : ""} {backgroundsWord(countValid ? range.count : 0)}
                </Button>
            </div>
        </section>
    );
}
