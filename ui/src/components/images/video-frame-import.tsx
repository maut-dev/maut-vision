import { useState, type FormEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { X } from "lucide-react";
import { imagesApi } from "@/api/projects/images/images-api";
import { isSelectedVideo, type SelectedVideoDto } from "@/api/projects/images/response-dto/video-selection-response-dto";
import { defaultVideoFrameRange, videoFrameRangeError, type VideoFrameRange } from "@/api/projects/images/video-frame-range";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { errorMessage } from "@/lib/format";
import "./locale";

/** Form fields in seconds; the request gets milliseconds. */
interface Fields {
    start: string;
    interval: string;
    count: string;
}

const fieldsOf = (range: VideoFrameRange): Fields => ({ start: String(range.startMs / 1000), interval: String(range.intervalMs / 1000), count: String(range.count) });

const rangeOf = (fields: Fields): VideoFrameRange => ({
    startMs: Math.round(Number(fields.start) * 1000),
    intervalMs: Math.round(Number(fields.interval) * 1000),
    count: Number(fields.count),
});

/**
 * "Frames from video": choosing a video in the native dialog, the marks (start, interval, count) and one import job. The import history
 * of the group shows the progress and the result. A choice serves one run: after it the video is chosen again.
 */
export function VideoFrameImport({ disabled, skipDuplicates, setSkipDuplicates, importFrames, close }: {
    disabled: boolean;
    skipDuplicates: boolean;
    setSkipDuplicates: (value: boolean) => void;
    importFrames: (selectionToken: string, range: VideoFrameRange, skipDuplicates: boolean) => Promise<void>;
    close: () => void;
}) {
    const { t } = useTranslation("images");
    const [video, setVideo] = useState<SelectedVideoDto>();
    const [fields, setFields] = useState<Fields>(fieldsOf(defaultVideoFrameRange(0)));
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const [started, setStarted] = useState(false);
    const range = rangeOf(fields);
    const rangeError = video ? videoFrameRangeError(range, video.durationMs) : null;

    async function choose() {
        setBusy(true);
        setError(undefined);
        try {
            const selected = await imagesApi.selectVideo();
            if (!isSelectedVideo(selected)) return;
            setVideo(selected);
            setFields(fieldsOf(defaultVideoFrameRange(selected.durationMs)));
            setStarted(false);
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    async function submit(event: FormEvent) {
        event.preventDefault();
        if (!video || rangeError || busy) return;
        setBusy(true);
        setError(undefined);
        try {
            await importFrames(video.selectionToken, range, skipDuplicates);
            setVideo(undefined);
            setStarted(true);
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const locked = disabled || busy;
    const field = (name: keyof Fields, label: string, props: { min: number; step: number }): ReactNode => (
        <label className="space-y-1">
            <span className="text-xs text-muted-foreground">{label}</span>
            <Input
                type="number"
                required
                disabled={!video || locked}
                data-testid={`video-${name}`}
                value={fields[name]}
                onChange={event => setFields(current => ({ ...current, [name]: event.target.value }))}
                {...props}
            />
        </label>
    );

    return (
        <section className="shrink-0 space-y-2 rounded-md border bg-card p-3" aria-label={t("video.title")} data-testid="video-frame-import">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <h3 className="text-sm font-medium">{t("video.title")}</h3>
                <p className="min-w-0 flex-1 truncate text-xs text-muted-foreground" data-testid="video-info">
                    {video
                        ? t("video.info", { name: video.displayName, width: video.width, height: video.height, seconds: (video.durationMs / 1000).toFixed(1) })
                        : started
                            ? t("video.started")
                            : t("video.choosePrompt")}
                </p>
                <Button size="sm" variant="outline" disabled={locked} data-testid="video-choose" onClick={() => void choose()}>
                    {video ? t("video.other") : t("video.choose")}
                </Button>
                <Button size="icon-sm" variant="ghost" aria-label={t("video.close")} disabled={busy} onClick={close}><X /></Button>
            </div>
            <form className="space-y-2" onSubmit={event => void submit(event)}>
                <div className="grid items-end gap-3 sm:grid-cols-[repeat(3,minmax(0,1fr))_auto]">
                    {field("start", t("video.start"), { min: 0, step: 0.001 })}
                    {field("interval", t("video.interval"), { min: 0.001, step: 0.001 })}
                    {field("count", t("video.count"), { min: 1, step: 1 })}
                    <Button type="submit" disabled={!video || locked || !!rangeError} data-testid="video-extract">{t("video.extract")}</Button>
                </div>
                <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
                    <span>{t("video.sizeNote")}</span>
                    <label className="flex items-center gap-2">
                        <Checkbox checked={skipDuplicates} disabled={locked} onCheckedChange={value => setSkipDuplicates(value === true)} data-testid="video-skip-duplicates" />
                        {t("section.skipDuplicates")}
                    </label>
                </div>
                <p role={rangeError || error ? "alert" : undefined} className="min-h-4 text-xs text-destructive" data-testid="video-alert">
                    {rangeError ? t(`video.rangeErrors.${rangeError === "video.range_invalid" ? "range_invalid" : "frames_invalid"}`) : error ?? ""}
                </p>
            </form>
        </section>
    );
}
