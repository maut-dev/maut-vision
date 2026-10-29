import { useState, type KeyboardEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { ChevronDown, ChevronRight } from "lucide-react";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundFileSelectionResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { errorMessage, formatNumber } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { BackgroundBlurSettings } from "./background-blur-settings";
import { BackgroundVideoImport } from "./background-video-import";
import "./locale";

export type ImportMode = "ready" | "images" | "video";

/** A file selection with a token (the dialog was not closed). */
export type FileSelection = BackgroundFileSelectionResponseDto & { selectionToken: string };

/** Ways of adding; their titles and hints are modes.<id> of the catalog. */
const modes: readonly ImportMode[] = ["ready", "images", "video"];

const number = (value: number) => formatNumber(value);

/**
 * "Add backgrounds": the way of adding (a radio group), its action and the shared blur. The panel folds so the gallery stays visible in
 * a small window; a folded panel keeps its state (the chosen video, the frame, the fields).
 */
export function BackgroundAddPanel({ projectId, set, blur, disabled, open, setOpen, start, openCrop }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    blur: BackgroundBlurState;
    /** An archived set or a running operation. */
    disabled: boolean;
    open: boolean;
    setOpen: (open: boolean) => void;
    start: (request: ImportBackgroundsRequestDto) => Promise<void>;
    openCrop: (selection: FileSelection) => void;
}) {
    const { t } = useTranslation("backgrounds");
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
        const next = modes[(modes.indexOf(mode) + step + modes.length) % modes.length];
        setMode(next);
        requestAnimationFrame(() => document.querySelector<HTMLElement>(`[data-import-mode="${next}"]`)?.focus());
    }

    return (
        <section className="flex max-h-[55%] min-h-0 shrink-0 flex-col overflow-hidden rounded-md border bg-card" aria-label={t("add.title")} data-testid="background-add">
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
                    <span className="block text-sm font-medium">{t("add.title")}</span>
                    <span className="block text-xs text-muted-foreground">{t("add.description", { width, height })}</span>
                </span>
            </button>
            <div id="background-add-body" hidden={!open} className="min-h-0 space-y-4 overflow-y-auto overscroll-contain px-3 pb-3">
                <fieldset disabled={disabled} className="space-y-4">
                    <div role="radiogroup" aria-label={t("add.modes")} className="grid gap-2 sm:grid-cols-3" onKeyDown={radioKey}>
                        {modes.map(item => (
                            <button
                                key={item}
                                type="button"
                                role="radio"
                                aria-checked={mode === item}
                                tabIndex={mode === item ? 0 : -1}
                                data-import-mode={item}
                                className={`rounded-md border px-3 py-2 text-left outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring ${mode === item ? "border-primary bg-accent text-accent-foreground" : "hover:bg-accent/50"}`}
                                onClick={() => setMode(item)}
                            >
                                <span className="block text-sm font-medium">{t(`modes.${item}.title`)}</span>
                                <span className="mt-1 block text-xs text-muted-foreground">{t(`modes.${item}.hint`)}</span>
                            </button>
                        ))}
                    </div>
                    {mode === "ready" && (
                        <div className="space-y-3" data-testid="ready-mode">
                            <div className="flex flex-wrap items-center justify-between gap-3">
                                <p className="min-w-0 flex-1 text-xs text-muted-foreground">
                                    {t("ready.note")}
                                </p>
                                <Button disabled={locked} data-testid="ready-choose" onClick={chooseReady}>{t("ready.choose")}</Button>
                            </div>
                            {ready && (
                                <div className="space-y-2 rounded-md border bg-background p-3 text-sm" data-testid="ready-summary">
                                    <p>
                                        <Trans
                                            t={t}
                                            i18nKey="ready.selected"
                                            values={{ number: number(ready.files.length), width, height, exact: number(ready.exact) }}
                                            components={{ exact: <span className="font-medium tabular-nums" data-testid="ready-exact" /> }}
                                        />
                                    </p>
                                    {ready.files.length > ready.exact && (
                                        <p className="text-muted-foreground" data-testid="ready-skipped">
                                            {t("ready.skipped", {
                                                number: number(ready.files.length - ready.exact),
                                                larger: number(ready.larger),
                                                smaller: number(ready.smaller),
                                                unreadable: number(ready.unreadable),
                                            })}
                                            {ready.larger > 0 && ` ${t("ready.largerHint")}`}
                                        </p>
                                    )}
                                    <div className="flex flex-wrap gap-2">
                                        <Button size="sm" disabled={locked || !ready.exact || !blur.valid} data-testid="ready-import" onClick={() => importReady(ready)}>
                                            {t("import", { count: ready.exact })}
                                        </Button>
                                        <Button size="sm" variant="outline" disabled={locked} onClick={() => setReady(undefined)}>{t("dialog.cancel")}</Button>
                                    </div>
                                </div>
                            )}
                        </div>
                    )}
                    {mode === "images" && (
                        <div className="flex flex-wrap items-center justify-between gap-3" data-testid="images-mode">
                            <p className="min-w-0 flex-1 text-xs text-muted-foreground">
                                {t("images.note", { width, height })}
                            </p>
                            <Button disabled={locked} data-testid="images-choose" onClick={chooseImages}>{t("images.choose")}</Button>
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
