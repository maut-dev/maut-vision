import { useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { maxCropSources } from "@/api/projects/backgrounds/background-import-rules";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundImportPreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-import-preview-response-dto";
import type { BackgroundFileSelectionResponseDto, BackgroundSourcePreviewResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { useReturnFocus } from "@/components/catalog/catalog-dialogs";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { automaticDecision, cropPlan, previousRegion, type CropDecision } from "@/lib/backgrounds/crop-plan";
import { defaultRegion, type FramePosition, type LastRegion } from "@/lib/backgrounds/crop-frame";
import { i18n } from "@/i18n";
import { errorMessage } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { BackgroundBlurSettings } from "./background-blur-settings";
import { BackgroundCropFrame } from "./background-crop-frame";
import { BackgroundImportSamples } from "./background-import-samples";
import "@/i18n/sections/backgrounds";

/** A frame preview is a temporary URL for 10 minutes; showing the file again reuses it while the expiry is far. */
const previewReuseMs = 5 * 60_000;

/**
 * "Cutting backgrounds from images": files one by one. Files of exactly the needed size are taken whole, smaller ones are skipped,
 * unreadable ones are listed, all by the headers of the selection without loading the image. On larger ones the user places a frame of
 * the set size: "Take region", "Skip file", "Back". Then a review with counters, blur and samples (the same operationId and pixels as
 * the run) and "Import N backgrounds".
 */
export function BackgroundImageCropDialog({ projectId, set, selection, blur, start, close }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    selection: BackgroundFileSelectionResponseDto & { selectionToken: string };
    blur: BackgroundBlurState;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
    close: () => void;
}) {
    const { t } = useTranslation("backgrounds");
    const frame = set.targetSize;
    const files = selection.files;
    const [operationId] = useState(() => crypto.randomUUID());
    const [stage, setStage] = useState<"pick" | "review">("pick");
    const [position, setPositionIndex] = useState(0);
    const [loading, setLoading] = useState(true);
    const [source, setSource] = useState<BackgroundSourcePreviewResponseDto>();
    const [region, setRegion] = useState<FramePosition>({ left: 0, top: 0 });
    const [decisions, setDecisions] = useState<ReadonlyMap<number, CropDecision>>(new Map());
    const [history, setHistory] = useState<readonly number[]>([]);
    const [preview, setPreview] = useState<BackgroundImportPreviewResponseDto>();
    const [samplesState, setSamplesState] = useState<{ busy: boolean; error?: string }>({ busy: false });
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const cache = useRef(new Map<number, { preview: BackgroundSourcePreviewResponseDto; at: number }>());
    const last = useRef<LastRegion | undefined>(undefined);
    const alive = useRef(true);
    // The pass number of advance: a stale pass (a double effect, "Back" during loading) quietly stops.
    const pass = useRef(0);
    const returnFocus = useReturnFocus();

    async function load(sourceIndex: number) {
        const cached = cache.current.get(sourceIndex);
        if (cached && Date.now() - cached.at < previewReuseMs) return cached.preview;
        const loaded = await backgroundsApi.previewSource({ projectId, selectionToken: selection.selectionToken, sourceIndex });
        cache.current.set(sourceIndex, { preview: loaded, at: Date.now() });
        return loaded;
    }

    /** Goes forward, deciding itself for files of the exact size, smaller and unreadable ones, up to the first file that needs a frame. */
    async function advance(from: number, known: ReadonlyMap<number, CropDecision>) {
        const token = ++pass.current;
        const current = () => alive.current && token === pass.current;
        const decided = new Map(known);
        setPreview(undefined);
        setError(undefined);
        for (let at = from; at < files.length; at++) {
            if (!current()) return;
            const file = files[at];
            setPositionIndex(at);
            const automatic = automaticDecision(file);
            if (automatic) {
                decided.set(file.index, automatic);
                continue;
            }
            setLoading(true);
            let loaded: BackgroundSourcePreviewResponseDto;
            try {
                loaded = await load(file.index);
            } catch (cause) {
                if (!current()) return;
                decided.set(file.index, { kind: "invalid", message: errorMessage(cause, i18n.t("backgrounds:crop.unreadable")) });
                setDecisions(new Map(decided));
                continue;
            }
            if (!current()) return;
            setDecisions(new Map(decided));
            setSource(loaded);
            setRegion(defaultRegion(loaded, frame, previousRegion(decided.get(file.index)), last.current));
            setLoading(false);
            setStage("pick");
            return;
        }
        if (!current()) return;
        setDecisions(decided);
        setLoading(false);
        setStage("review");
    }

    useEffect(() => {
        alive.current = true;
        void advance(0, new Map());
        return () => { alive.current = false; };
        // The walk starts once per selection; advance reads the latest state through its arguments.
    }, []);

    function decide(decision: CropDecision) {
        const file = files[position];
        const next = new Map(decisions);
        next.set(file.index, decision);
        if (decision.kind === "crop" && source) last.current = { width: source.width, height: source.height, at: { left: decision.left, top: decision.top } };
        setDecisions(next);
        setHistory(value => [...value, position]);
        void advance(position + 1, next);
    }

    function back() {
        const previous = history[history.length - 1];
        if (previous === undefined) return;
        setHistory(value => value.slice(0, -1));
        void advance(previous, decisions);
    }

    const plan = useMemo(() => cropPlan(decisions), [decisions]);
    const included = plan.sourceIndices;
    const tooMany = included.length > maxCropSources;
    const request: ImportBackgroundsRequestDto = useMemo(() => ({
        projectId,
        setId: set.id,
        selectionToken: selection.selectionToken,
        operationId,
        sourceIndices: plan.sourceIndices,
        crops: plan.crops,
        blur: blur.value,
    }), [projectId, set.id, selection.selectionToken, operationId, plan, blur.value]);
    const requestKey = JSON.stringify(request);

    // Samples are recomputed when decisions or the blur change: the user sees exactly what will be imported.
    useEffect(() => {
        if (stage !== "review" || !included.length || tooMany || !blur.valid) return;
        const controller = new AbortController();
        setSamplesState({ busy: true });
        const timer = window.setTimeout(() => {
            backgroundsApi.previewImport(request, controller.signal).then(
                result => { if (!controller.signal.aborted) { setPreview(result); setSamplesState({ busy: false }); } },
                cause => { if (!controller.signal.aborted) setSamplesState({ busy: false, error: errorMessage(cause) }); });
        }, 300);
        return () => {
            window.clearTimeout(timer);
            controller.abort();
        };
        // requestKey covers the request fields.
    }, [stage, requestKey, blur.valid, tooMany]);

    async function importAll() {
        setBusy(true);
        setError(undefined);
        try {
            await start(request);
            close();
        } catch (cause) {
            if (alive.current) setError(errorMessage(cause));
        } finally {
            if (alive.current) setBusy(false);
        }
    }

    const name = (sourceIndex: number) => files.find(file => file.index === sourceIndex)?.name ?? `#${sourceIndex + 1}`;
    const listed = (kind: "small" | "invalid") => [...decisions.entries()].filter(([, decision]) => decision.kind === kind).sort(([left], [right]) => left - right);
    const small = listed("small");
    const invalid = listed("invalid");

    return (
        <Dialog open onOpenChange={open => { if (!open && !busy) close(); }}>
            <DialogContent
                className="max-h-[94vh] overflow-y-auto sm:max-w-4xl"
                data-testid="crop-dialog"
                data-stage={stage}
                showCloseButton={!busy}
                onEscapeKeyDown={event => { if (busy) event.preventDefault(); }}
                onInteractOutside={event => { if (busy) event.preventDefault(); }}
                onCloseAutoFocus={returnFocus}
            >
                <DialogHeader>
                    <DialogTitle>{t("crop.title")}</DialogTitle>
                    <DialogDescription>
                        {stage === "pick"
                            ? t("crop.pickHint", { position: position + 1, total: files.length, width: frame.width, height: frame.height })
                            : t("crop.reviewHint")}
                    </DialogDescription>
                </DialogHeader>
                {stage === "pick" ? (
                    <div className="space-y-3">
                        <p className="min-h-5 truncate text-sm" data-testid="crop-file">{files[position]?.name}</p>
                        {loading || !source ? (
                            <div role="status" className="flex h-72 items-center justify-center rounded-md bg-muted text-xs text-muted-foreground">{t("crop.loading")}</div>
                        ) : (
                            <BackgroundCropFrame source={source} frame={frame} value={region} onChange={setRegion} autoFocus />
                        )}
                    </div>
                ) : (
                    <div className="space-y-4" data-testid="crop-review">
                        <ul className="space-y-1 text-sm">
                            <li data-testid="crop-count-crop">{t("crop.countCrop", { number: plan.counts.crop })}</li>
                            <li data-testid="crop-count-whole">{t("crop.countWhole", { width: frame.width, height: frame.height, number: plan.counts.whole })}</li>
                            <li className="text-muted-foreground" data-testid="crop-count-skip">{t("crop.countSkip", { number: plan.counts.skip })}</li>
                        </ul>
                        {small.length > 0 && (
                            <details className="rounded-md border px-3 py-2 text-xs text-muted-foreground" data-testid="crop-small">
                                <summary className="cursor-pointer select-none">{t("crop.small", { number: small.length })}</summary>
                                {small.map(([index, decision]) => decision.kind === "small" && (
                                    <p key={index} className="mt-1 break-words">
                                        {t("crop.smallFile", { name: name(index), width: decision.width, height: decision.height, targetWidth: frame.width, targetHeight: frame.height })}
                                    </p>
                                ))}
                            </details>
                        )}
                        {invalid.length > 0 && (
                            <details className="rounded-md border px-3 py-2 text-xs text-destructive" data-testid="crop-invalid">
                                <summary className="cursor-pointer select-none">{t("crop.invalid", { number: invalid.length })}</summary>
                                {invalid.map(([index, decision]) => decision.kind === "invalid" && (
                                    <p key={index} className="mt-1 break-words">{name(index)} · {decision.message ?? t("crop.unreadable")}</p>
                                ))}
                            </details>
                        )}
                        <BackgroundBlurSettings blur={blur} disabled={busy} />
                        {tooMany ? (
                            <p role="alert" className="text-sm text-destructive">
                                {t("crop.tooMany", { count: maxCropSources })}
                            </p>
                        ) : included.length ? (
                            <div aria-busy={samplesState.busy}>
                                {samplesState.error && <Alert variant="destructive"><AlertDescription>{samplesState.error}</AlertDescription></Alert>}
                                {samplesState.busy && !preview && <p role="status" className="text-xs text-muted-foreground">{t("crop.preparingSamples")}</p>}
                                <BackgroundImportSamples preview={preview} />
                            </div>
                        ) : (
                            <p className="text-sm text-muted-foreground">{t("crop.nothing")}</p>
                        )}
                    </div>
                )}
                <div aria-live="polite">
                    {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <DialogFooter>
                    {stage === "pick" ? (
                        <>
                            <Button variant="ghost" disabled={loading || !history.length} onClick={back}>{t("crop.back")}</Button>
                            <Button variant="outline" disabled={loading} data-testid="crop-skip" onClick={() => decide({ kind: "skip" })}>{t("crop.skip")}</Button>
                            <Button disabled={loading || !source} data-testid="crop-take" onClick={() => decide({ kind: "crop", ...region })}>{t("crop.take")}</Button>
                        </>
                    ) : (
                        <>
                            <Button variant="ghost" disabled={busy || !history.length} onClick={back}>{t("crop.back")}</Button>
                            <Button disabled={busy || !included.length || tooMany || !blur.valid} data-testid="crop-import" onClick={() => void importAll()}>
                                {t("import", { count: included.length })}
                            </Button>
                        </>
                    )}
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
