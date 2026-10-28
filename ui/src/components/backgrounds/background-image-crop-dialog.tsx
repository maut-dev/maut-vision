import { useEffect, useMemo, useRef, useState } from "react";
import { backgroundsWord, maxCropSources } from "@/api/projects/backgrounds/background-import-rules";
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
import { errorMessage } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { BackgroundBlurSettings } from "./background-blur-settings";
import { BackgroundCropFrame } from "./background-crop-frame";
import { BackgroundImportSamples } from "./background-import-samples";

/** Превью кадра — временный URL на 10 минут; повторный показ файла берёт его, пока до конца срока далеко. */
const previewReuseMs = 5 * 60_000;

/**
 * «Нарезка фонов из изображений»: файлы по одному. Ровно нужного размера берутся целиком, меньшие пропускаются,
 * нечитаемые показываются — по заголовкам выбора, без загрузки кадра. На больших пользователь ставит рамку размера
 * набора: «Взять область», «Пропустить файл», «Назад». Затем проверка со счётчиками, размытием и примерами (те же
 * operationId и пиксели, что у запуска) и «Импортировать N фонов».
 */
export function BackgroundImageCropDialog({ projectId, set, selection, blur, start, close }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    selection: BackgroundFileSelectionResponseDto & { selectionToken: string };
    blur: BackgroundBlurState;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
    close: () => void;
}) {
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
    // Номер прохода advance: устаревший проход (двойной эффект, «Назад» во время загрузки) молча выходит.
    const pass = useRef(0);
    const returnFocus = useReturnFocus();

    async function load(sourceIndex: number) {
        const cached = cache.current.get(sourceIndex);
        if (cached && Date.now() - cached.at < previewReuseMs) return cached.preview;
        const loaded = await backgroundsApi.previewSource({ projectId, selectionToken: selection.selectionToken, sourceIndex });
        cache.current.set(sourceIndex, { preview: loaded, at: Date.now() });
        return loaded;
    }

    /** Идёт вперёд, сам решая за файлы точного размера, меньшие и нечитаемые, до первого файла, где нужна рамка. */
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
                decided.set(file.index, { kind: "invalid", message: errorMessage(cause, "Не удалось прочитать изображение") });
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

    // Примеры пересчитываются при смене решений и размытия: пользователь видит ровно то, что будет импортировано.
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
                    <DialogTitle>Нарезка фонов из изображений</DialogTitle>
                    <DialogDescription>
                        {stage === "pick"
                            ? `Файл ${position + 1} из ${files.length}. Перетащите рамку ${frame.width} × ${frame.height} px на нужный участок: размер рамки равен входу модели и не меняется. Стрелки двигают рамку на 1 px, с Shift — на 10 px.`
                            : "Проверьте примеры и запустите импорт. Нарезанные фоны появятся в наборе по мере обработки."}
                    </DialogDescription>
                </DialogHeader>
                {stage === "pick" ? (
                    <div className="space-y-3">
                        <p className="min-h-5 truncate text-sm" data-testid="crop-file">{files[position]?.name}</p>
                        {loading || !source ? (
                            <div role="status" className="flex h-72 items-center justify-center rounded-md bg-muted text-xs text-muted-foreground">Загрузка кадра…</div>
                        ) : (
                            <BackgroundCropFrame source={source} frame={frame} value={region} onChange={setRegion} autoFocus />
                        )}
                    </div>
                ) : (
                    <div className="space-y-4" data-testid="crop-review">
                        <ul className="space-y-1 text-sm">
                            <li data-testid="crop-count-crop">Нарезать по рамке: {plan.counts.crop}</li>
                            <li data-testid="crop-count-whole">Взять целиком (ровно {frame.width} × {frame.height}): {plan.counts.whole}</li>
                            <li className="text-muted-foreground" data-testid="crop-count-skip">Пропущено вами: {plan.counts.skip}</li>
                        </ul>
                        {small.length > 0 && (
                            <details className="rounded-md border px-3 py-2 text-xs text-muted-foreground" data-testid="crop-small">
                                <summary className="cursor-pointer select-none">Меньше входа модели, пропущено: {small.length}</summary>
                                {small.map(([index, decision]) => decision.kind === "small" && (
                                    <p key={index} className="mt-1 break-words">
                                        {name(index)} · размер {decision.width}×{decision.height}, нужен не меньше {frame.width}×{frame.height}
                                    </p>
                                ))}
                            </details>
                        )}
                        {invalid.length > 0 && (
                            <details className="rounded-md border px-3 py-2 text-xs text-destructive" data-testid="crop-invalid">
                                <summary className="cursor-pointer select-none">Не прочитано: {invalid.length}</summary>
                                {invalid.map(([index, decision]) => decision.kind === "invalid" && (
                                    <p key={index} className="mt-1 break-words">{name(index)} · {decision.message}</p>
                                ))}
                            </details>
                        )}
                        <BackgroundBlurSettings blur={blur} disabled={busy} />
                        {tooMany ? (
                            <p role="alert" className="text-sm text-destructive">
                                За один запуск можно нарезать до {maxCropSources} файлов. Пропустите часть файлов или запустите нарезку несколько раз.
                            </p>
                        ) : included.length ? (
                            <div aria-busy={samplesState.busy}>
                                {samplesState.error && <Alert variant="destructive"><AlertDescription>{samplesState.error}</AlertDescription></Alert>}
                                {samplesState.busy && !preview && <p role="status" className="text-xs text-muted-foreground">Готовим примеры…</p>}
                                <BackgroundImportSamples preview={preview} />
                            </div>
                        ) : (
                            <p className="text-sm text-muted-foreground">Нечего импортировать: ни один файл не выбран.</p>
                        )}
                    </div>
                )}
                <div aria-live="polite">
                    {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                </div>
                <DialogFooter>
                    {stage === "pick" ? (
                        <>
                            <Button variant="ghost" disabled={loading || !history.length} onClick={back}>Назад</Button>
                            <Button variant="outline" disabled={loading} data-testid="crop-skip" onClick={() => decide({ kind: "skip" })}>Пропустить файл</Button>
                            <Button disabled={loading || !source} data-testid="crop-take" onClick={() => decide({ kind: "crop", ...region })}>Взять область</Button>
                        </>
                    ) : (
                        <>
                            <Button variant="ghost" disabled={busy || !history.length} onClick={back}>Назад</Button>
                            <Button disabled={busy || !included.length || tooMany || !blur.valid} data-testid="crop-import" onClick={() => void importAll()}>
                                Импортировать {included.length} {backgroundsWord(included.length)}
                            </Button>
                        </>
                    )}
                </DialogFooter>
            </DialogContent>
        </Dialog>
    );
}
