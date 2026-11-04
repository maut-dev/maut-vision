import { useState, type KeyboardEvent } from "react";
import { Trans, useTranslation } from "react-i18next";
import { backgroundsApi } from "@/api/projects/backgrounds/backgrounds-api";
import type { ImportBackgroundsRequestDto } from "@/api/projects/backgrounds/request-dto/import-backgrounds-request-dto";
import type { BackgroundFileSelectionResponseDto } from "@/api/projects/backgrounds/response-dto/background-selection-response-dto";
import type { BackgroundSetResponseDto } from "@/api/projects/backgrounds/response-dto/background-set-response-dto";
import { Notice } from "@/components/layout/notice";
import { Pane } from "@/components/layout/pane";
import { Button } from "@/components/ui/button";
import { errorMessage, formatNumber } from "@/lib/format";
import type { BackgroundBlurState } from "@/lib/use-background-blur";
import { BackgroundBlurSettings } from "./background-blur-settings";
import { BackgroundVideoImport } from "./background-video-import";
import "@/i18n/sections/backgrounds";

export type ImportMode = "ready" | "images" | "video";

/** A file selection with a token (the dialog was not closed). */
export type FileSelection = BackgroundFileSelectionResponseDto & { selectionToken: string };

/** Ways of adding; their titles and hints are modes.<id> of the catalog. */
const modes: readonly ImportMode[] = ["ready", "images", "video"];

const number = (value: number) => formatNumber(value);

/**
 * "Add backgrounds": the way of adding (a radio group), its action and the shared blur, in one scrolling pane. The section keeps the
 * panel mounted while another tab is shown, so its state (the chosen video, the frame, the fields) survives switching tabs.
 */
export function BackgroundAddPanel({ projectId, set, blur, disabled, start, openCrop }: {
    projectId: string;
    set: BackgroundSetResponseDto;
    blur: BackgroundBlurState;
    /** An archived set or a running operation. */
    disabled: boolean;
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
        <Pane surface role="region" className="@container flex-1 space-y-4 p-4" aria-label={t("add.title")} data-testid="background-add">
            <div className="space-y-1">
                <h3 className="text-sm font-medium">{t("add.title")}</h3>
                <p className="text-xs text-text-2">{t("add.description", { width, height })}</p>
            </div>
            <fieldset disabled={disabled} className="min-w-0 space-y-4">
                <div role="radiogroup" aria-label={t("add.modes")} className="grid gap-2 @lg:grid-cols-3" onKeyDown={radioKey}>
                    {modes.map(item => (
                        <button
                            key={item}
                            type="button"
                            role="radio"
                            aria-checked={mode === item}
                            tabIndex={mode === item ? 0 : -1}
                            data-import-mode={item}
                            className={`rounded-md border px-3 py-2 text-left outline-none transition-colors duration-120 ease-glass focus-visible:ring-[3px] focus-visible:ring-focus/30 ${mode === item ? "border-line-strong bg-glass-fill-hover shadow-[var(--glass-highlight)]" : "border-line hover:bg-glass-fill"}`}
                            onClick={() => setMode(item)}
                        >
                            <span className="block text-sm font-medium">{t(`modes.${item}.title`)}</span>
                            <span className="mt-1 block text-xs text-text-2">{t(`modes.${item}.hint`)}</span>
                        </button>
                    ))}
                </div>
                {mode === "ready" && (
                    <div className="space-y-3" data-testid="ready-mode">
                        <div className="flex flex-wrap items-center justify-between gap-3">
                            <p className="min-w-0 flex-1 text-xs text-text-2">
                                {t("ready.note")}
                            </p>
                            <Button disabled={locked} data-testid="ready-choose" onClick={chooseReady}>{t("ready.choose")}</Button>
                        </div>
                        {ready && (
                            <div className="space-y-2 rounded-lg border border-line bg-ink-2 p-3 text-sm" data-testid="ready-summary">
                                <p>
                                    <Trans
                                        t={t}
                                        i18nKey="ready.selected"
                                        values={{ number: number(ready.files.length), width, height, exact: number(ready.exact) }}
                                        components={{ exact: <span className="font-medium tabular-nums" data-testid="ready-exact" /> }}
                                    />
                                </p>
                                {ready.files.length > ready.exact && (
                                    <p className="text-text-2" data-testid="ready-skipped">
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
                                    <Button size="sm" variant="brand" disabled={locked || !ready.exact || !blur.valid} data-testid="ready-import" onClick={() => importReady(ready)}>
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
                        <p className="min-w-0 flex-1 text-xs text-text-2">
                            {t("images.note", { width, height })}
                        </p>
                        <Button variant="brand" disabled={locked} data-testid="images-choose" onClick={chooseImages}>{t("images.choose")}</Button>
                    </div>
                )}
                <div hidden={mode !== "video"}>
                    <BackgroundVideoImport projectId={projectId} set={set} blur={blur} disabled={locked} start={start} />
                </div>
            </fieldset>
            {error && <Notice variant="destructive">{error}</Notice>}
            <BackgroundBlurSettings blur={blur} disabled={locked} />
        </Pane>
    );
}
