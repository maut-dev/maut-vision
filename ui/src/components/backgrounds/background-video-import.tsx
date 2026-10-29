import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
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
import "@/i18n/sections/backgrounds";

/**
 * "Cut from video": one frame of the set size for the whole video. The video is chosen in the native dialog (without hashing), the
 * example frame is the middle of the video, "Show another frame" takes a random mark. The fields are checked with the same rules as the
 * host's; samples and the run share one operationId. A choice serves one run: after it the video is chosen again.
 */
export function BackgroundVideoImport({ projectId, set, blur, disabled, start }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    blur: BackgroundBlurState;
    disabled: boolean;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
}) {
    const { t } = useTranslation("backgrounds");
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
    /** The shown example frame: another frame of the same size keeps the crop frame. */
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

    // A change of the parameters makes the samples stale.
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
            // A choice belongs to a run: the next run needs a new video and a new operation.
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
        <section className="space-y-4" aria-label={t("video.title")} data-testid="bg-video-import">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                    <h4 className="text-sm font-medium">{t("video.title")}</h4>
                    <p className="text-xs text-muted-foreground">{t("video.description", { width: frame.width, height: frame.height })}</p>
                </div>
                <Button variant="outline" size="sm" disabled={locked} data-testid="bg-video-choose" onClick={choose}>{video ? t("video.other") : t("video.choose")}</Button>
            </div>
            <p className="min-h-5 truncate text-xs text-muted-foreground" data-testid="bg-video-info">
                {video
                    ? t("video.info", { name: video.displayName, width: video.width, height: video.height, seconds: (video.durationMs / 1000).toFixed(1) })
                    : started
                        ? t("video.started")
                        : t("video.choosePrompt")}
            </p>
            {video && example && (
                <div className="space-y-2">
                    <BackgroundCropFrame source={example} frame={frame} value={region} onChange={setRegion} disabled={locked} maxHeight="40vh" />
                    <div className="flex justify-center">
                        <Button variant="ghost" size="sm" disabled={locked} data-testid="bg-video-another"
                            onClick={() => void act(() => showExample(video, Math.floor(Math.random() * Math.max(1, video.durationMs - 1))))}>
                            {t("video.another")}
                        </Button>
                    </div>
                </div>
            )}
            <div className="grid gap-3 sm:grid-cols-2">
                {field("count", t("video.count"), { min: 1, step: 1 })}
                {field("interval", t("video.interval"), { min: 0.001, step: "any" })}
            </div>
            <label className="flex items-start gap-2 text-sm">
                <Checkbox className="mt-0.5" disabled={!video || locked} checked={fields.skipSimilar} data-testid="bg-video-skip-similar"
                    onCheckedChange={checked => setFields(current => ({ ...current, skipSimilar: checked === true }))} />
                <span>
                    {t("video.skipSimilar")}
                    <span className="block text-xs text-muted-foreground">{t("video.skipSimilarHint")}</span>
                </span>
            </label>
            <details className="rounded-md border px-3 py-2">
                <summary className="cursor-pointer text-sm text-muted-foreground select-none">{t("advanced")}</summary>
                <div className="mt-3 grid gap-3 sm:grid-cols-[12rem_minmax(0,1fr)] sm:items-end">
                    {field("start", t("video.start"), { min: 0, step: "any" })}
                    <p className="pb-2 text-xs text-muted-foreground">{t("video.startHint")}</p>
                </div>
            </details>
            <p role={message ? "alert" : undefined} className={`min-h-5 text-xs ${message ? "text-destructive" : "text-muted-foreground"}`} data-testid="bg-video-message">
                {message ?? (video && available ? t("video.available", { count: available }) : "")}
            </p>
            {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
            {busy && !preview && video && <p role="status" className="text-xs text-muted-foreground">{t("video.preparing")}</p>}
            <BackgroundImportSamples preview={preview} />
            <div className="flex flex-wrap gap-2">
                <Button variant="outline" disabled={!ready || locked} data-testid="bg-video-samples" onClick={showSamples}>{t("video.samples")}</Button>
                <Button disabled={!ready || locked} data-testid="bg-video-run" onClick={launch}>
                    {countValid ? t("video.run", { count: range.count }) : t("video.runEmpty")}
                </Button>
            </div>
        </section>
    );
}
