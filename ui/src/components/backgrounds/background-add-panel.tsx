import { useState, type KeyboardEvent } from "react";
import { ChevronDown, ChevronRight } from "lucide-react";
import { backgroundsWord } from "@/api/projects/backgrounds/background-import-rules";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundFileSelectionResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { errorMessage } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { BackgroundBlurSettings } from "./background-blur-settings";
import { BackgroundVideoImport } from "./background-video-import";

export type ImportMode = "ready" | "images" | "video";

/** Выбор файлов с токеном (диалог не закрыт). */
export type FileSelection = BackgroundFileSelectionResponseDto & { selectionToken: string };

const modes: readonly { id: ImportMode; title: string; hint: string }[] = [
    { id: "ready", title: "Готовые кадры", hint: "Файлы уже нужного размера — импорт как есть." },
    { id: "images", title: "Нарезать из изображений", hint: "Крупные скриншоты: рамку ставите на каждом файле." },
    { id: "video", title: "Нарезать из видео", hint: "Одна рамка на всё видео, кадр раз в N секунд." },
];

const number = (value: number) => value.toLocaleString("ru-RU");

/**
 * «Добавить фоны»: способ добавления (радиогруппа), его действие и общее размытие. Панель сворачивается, чтобы
 * галерея оставалась видна в небольшом окне; свёрнутая панель сохраняет состояние (выбранное видео, рамку, поля).
 */
export function BackgroundAddPanel({ projectId, set, blur, disabled, open, setOpen, start, openCrop }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    blur: BackgroundBlurState;
    /** Архивный набор или идёт операция. */
    disabled: boolean;
    open: boolean;
    setOpen: (open: boolean) => void;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
    openCrop: (selection: FileSelection) => void;
}) {
    const [mode, setMode] = useState<ImportMode>("ready");
    const [ready, setReady] = useState<FileSelection>();
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string>();
    const { width, height } = set.targetSize;
    const locked = disabled || busy;

    async function act(action: () => Promise<void>) {
        setBusy(true);
        setError(undefined);
        try {
            await action();
        } catch (cause) {
            setError(errorMessage(cause));
        } finally {
            setBusy(false);
        }
    }

    const select = () => backgroundsApi.selectFiles({ projectId, setId: set.id }).then(selection => selection.selectionToken ? selection as FileSelection : undefined);

    function chooseReady() {
        void act(async () => {
            const selection = await select();
            if (selection) setReady(selection);
        });
    }

    function importReady(selection: FileSelection) {
        void act(async () => {
            await start({ projectId, setId: set.id, selectionToken: selection.selectionToken, operationId: crypto.randomUUID(), blur: blur.value });
            setReady(undefined);
        });
    }

    function chooseImages() {
        void act(async () => {
            const selection = await select();
            if (selection) openCrop(selection);
        });
    }

    function radioKey(event: KeyboardEvent) {
        const step = event.key === "ArrowRight" || event.key === "ArrowDown" ? 1 : event.key === "ArrowLeft" || event.key === "ArrowUp" ? -1 : 0;
        if (!step) return;
        event.preventDefault();
        const next = modes[(modes.findIndex(item => item.id === mode) + step + modes.length) % modes.length].id;
        setMode(next);
        requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-import-mode="${next}"]`)?.focus());
    }

    return (
        <section className="flex max-h-[55%] min-h-0 shrink-0 flex-col overflow-hidden rounded-md border bg-card" aria-label="Добавить фоны" data-testid="background-add">
            <button
                type="button"
                className="flex w-full items-start gap-2 px-3 py-2 text-left outline-none hover:bg-accent/40 focus-visible:ring-2 focus-visible:ring-ring"
                aria-expanded={open}
                aria-controls="background-add-body"
                data-testid="background-add-toggle"
                onClick={() => setOpen(!open)}
            >
                {open ? <ChevronDown className="mt-0.5 size-4 shrink-0" aria-hidden /> : <ChevronRight className="mt-0.5 size-4 shrink-0" aria-hidden />}
                <span className="min-w-0">
                    <span className="block text-sm font-medium">Добавить фоны</span>
                    <span className="block text-xs text-muted-foreground">
                        Каждый фон — картинка ровно {width} × {height} px, как вход модели. На фоны при генерации наклеиваются объекты; разметка выполняется в
                        разделе изображений.
                    </span>
                </span>
            </button>
            <div id="background-add-body" hidden={!open} className="min-h-0 space-y-4 overflow-y-auto overscroll-contain px-3 pb-3">
                <fieldset disabled={disabled} className="space-y-4">
                    <div role="radiogroup" aria-label="Способ добавления фонов" className="grid gap-2 sm:grid-cols-3" onKeyDown={radioKey}>
                        {modes.map(item => (
                            <button
                                key={item.id}
                                type="button"
                                role="radio"
                                aria-checked={mode === item.id}
                                tabIndex={mode === item.id ? 0 : -1}
                                data-import-mode={item.id}
                                className={`rounded-md border px-3 py-2 text-left outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring ${mode === item.id ? "border-primary bg-accent text-accent-foreground" : "hover:bg-accent/50"}`}
                                onClick={() => setMode(item.id)}
                            >
                                <span className="block text-sm font-medium">{item.title}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">{item.hint}</span>
                            </button>
                        ))}
                    </div>
                    {mode === "ready" && (
                        <div className="space-y-3" data-testid="ready-mode">
                            <div className="flex flex-wrap items-center justify-between gap-3">
                                <p className="min-w-0 flex-1 text-xs text-muted-foreground">
                                    Файлы другого размера не импортируются: они попадут в «Причины пропуска» запуска с фактическим размером.
                                </p>
                                <Button disabled={locked} data-testid="ready-choose" onClick={chooseReady}>Выбрать файлы</Button>
                            </div>
                            {ready && (
                                <div className="space-y-2 rounded-md border bg-background p-3 text-sm" data-testid="ready-summary">
                                    <p>
                                        Выбрано файлов: {number(ready.files.length)}. Нужного размера {width} × {height}:{" "}
                                        <span className="font-medium tabular-nums" data-testid="ready-exact">{number(ready.exact)}</span> — будут импортированы.
                                    </p>
                                    {ready.files.length > ready.exact && (
                                        <p className="text-muted-foreground" data-testid="ready-skipped">
                                            Будут пропущены: {number(ready.files.length - ready.exact)}
                                            {" "}(больше — {number(ready.larger)}, меньше — {number(ready.smaller)}, не читаются — {number(ready.unreadable)}).
                                            {ready.larger > 0 && " Крупные файлы можно нарезать рамкой: «Нарезать из изображений»."}
                                        </p>
                                    )}
                                    <div className="flex flex-wrap gap-2">
                                        <Button size="sm" disabled={locked || !ready.exact || !blur.valid} data-testid="ready-import" onClick={() => importReady(ready)}>
                                            Импортировать {number(ready.exact)} {backgroundsWord(ready.exact)}
                                        </Button>
                                        <Button size="sm" variant="outline" disabled={locked} onClick={() => setReady(undefined)}>Отмена</Button>
                                    </div>
                                </div>
                            )}
                        </div>
                    )}
                    {mode === "images" && (
                        <div className="flex flex-wrap items-center justify-between gap-3" data-testid="images-mode">
                            <p className="min-w-0 flex-1 text-xs text-muted-foreground">
                                Файлы покажутся по одному: на каждом поставьте рамку {width} × {height} px. Файлы ровно этого размера берутся целиком, меньшие —
                                пропускаются. Перед импортом будут примеры.
                            </p>
                            <Button disabled={locked} data-testid="images-choose" onClick={chooseImages}>Выбрать изображения</Button>
                        </div>
                    )}
                    <div hidden={mode !== "video"}>
                        <BackgroundVideoImport projectId={projectId} set={set} blur={blur} disabled={locked} start={start} />
                    </div>
                </fieldset>
                {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
                <BackgroundBlurSettings blur={blur} disabled={locked} />
            </div>
        </section>
    );
}
