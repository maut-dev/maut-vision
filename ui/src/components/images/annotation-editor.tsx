import { useCallback, useEffect, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, Redo2, Undo2 } from "lucide-react";
import type { ClassVariantResponseDto } from "@/api/projects/catalog/response-dto/class-variant-response-dto";
import { imagesApi } from "@/api/projects/images/images-api";
import type { ImageAnnotationResponseDto, SavedImageAnnotationResponseDto } from "@/api/projects/images/response-dto/image-annotation-response-dto";
import type { ImageResponseDto } from "@/api/projects/images/response-dto/image-response-dto";
import { VariantColorInput } from "@/components/catalog/variant-color-input";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useUnsavedChanges } from "@/components/unsaved-changes";
import { isTypingTarget, useZoomViewport } from "@/components/viewer/use-zoom-viewport";
import { ZoomControls } from "@/components/viewer/zoom-controls";
import { draftOf, isDraftChanged, removeObject, saveRequest, updateObject, type AnnotationDraft } from "@/lib/annotation/draft";
import { canRedo, canUndo, createHistory, record, redo, undo, type History } from "@/lib/annotation/history";
import { errorMessage } from "@/lib/format";
import { AnnotationCanvas, type AnnotationTool, type Selection } from "./annotation-canvas";
import type { AnnotationPalette } from "./annotation-palette";
import { ImageDetails } from "./image-details";

const noVariant = "none";

/** Открыт диалог или список выбора: клавиши редактора не перехватываются. */
const overlayOpen = () => !!document.querySelector("[role='dialog'],[role='alertdialog'],[role='listbox'],[role='menu']");

export interface EditorNavigation {
    previous?: () => void;
    next?: () => void;
}

/**
 * Редактор разметки одного изображения. Черновик живёт в истории правок; несохранённым он считается, только если
 * отличается от сохранённой ревизии (отмена до сохранённого состояния снимает признак). Защита несохранённых
 * изменений общая для приложения: редактор регистрирует сохранение и отказ от изменений.
 */
export function AnnotationEditor({ projectId, image, palette, lockReason, navigation, close, inspect, saved, setColor }: {
    projectId: string;
    image: ImageResponseDto;
    palette: AnnotationPalette;
    /** Причина, по которой разметка только для просмотра (генерация или архив). */
    lockReason?: string;
    navigation: EditorNavigation;
    close: () => void;
    /** «Просмотр и масштаб»: просмотрщик с текущим черновиком. */
    inspect: (draft: AnnotationDraft | undefined, revision: number | undefined) => void;
    saved: (result: SavedImageAnnotationResponseDto) => void;
    setColor: (binding: ClassVariantResponseDto, color: string | null) => void;
}) {
    const [stored, setStored] = useState<ImageAnnotationResponseDto>();
    const [history, setHistory] = useState<History<AnnotationDraft>>();
    const [loadError, setLoadError] = useState<string>();
    const [error, setError] = useState<string>();
    const [busy, setBusy] = useState(false);
    const [selection, setSelection] = useState<Selection>(null);
    const [tool, setTool] = useState<AnnotationTool>("object");
    const [classId, setClassId] = useState(() => palette.activeClasses[0]?.id ?? "");
    const [classVariantId, setClassVariantId] = useState<string | null>(null);
    const [attempt, setAttempt] = useState(0);
    const [source, setSource] = useState(image);
    const zoom = useZoomViewport(image, image.id);
    const locked = !!lockReason;
    const draft = history?.present;
    const dirty = !!stored && !!draft && isDraftChanged(draft, draftOf(stored));
    const selectedObject = selection && selection !== "roi" ? draft?.objects.find(item => item.id === selection) : undefined;
    const effectiveClassId = selectedObject?.classId ?? classId;
    const effectiveVariantId = selectedObject ? selectedObject.classVariantId : classVariantId;
    const binding = effectiveVariantId ? palette.binding(effectiveVariantId) : undefined;

    useEffect(() => { setSource(image); }, [image]);

    useEffect(() => {
        if (!classId && palette.activeClasses.length) setClassId(palette.activeClasses[0].id);
    }, [classId, palette.activeClasses]);

    useEffect(() => {
        const controller = new AbortController();
        setLoadError(undefined);
        imagesApi.getAnnotation({ projectId, imageId: image.id }, controller.signal).then(
            annotation => {
                setStored(annotation);
                setHistory(createHistory(draftOf(annotation)));
                setSelection(null);
            },
            cause => { if (!controller.signal.aborted) setLoadError(errorMessage(cause, "Не удалось загрузить разметку. Повторите загрузку.")); });
        return () => controller.abort();
    }, [projectId, image.id, attempt]);

    const save = useCallback(async () => {
        if (!stored || !draft) throw new Error("Разметка ещё загружается. Повторите сохранение.");
        if (locked) throw new Error(lockReason);
        setBusy(true);
        setError(undefined);
        try {
            const result = await imagesApi.saveAnnotation(saveRequest(projectId, image.id, stored.revision, draft));
            setStored(result.annotation);
            setHistory(createHistory(draftOf(result.annotation)));
            setSelection(null);
            saved(result);
        } catch (cause) {
            const message = errorMessage(cause, "Не удалось сохранить разметку.");
            setError(message);
            throw new Error(message);
        } finally {
            setBusy(false);
        }
    }, [stored, draft, locked, lockReason, projectId, image.id, saved]);

    const discard = useCallback(() => {
        if (stored) setHistory(createHistory(draftOf(stored)));
        setSelection(null);
    }, [stored]);

    useUnsavedChanges(dirty, save, discard);

    const change = useCallback((next: AnnotationDraft, nextSelection: Selection = selection) => {
        if (locked) return;
        setHistory(current => current && record(current, next));
        setSelection(nextSelection);
    }, [locked, selection]);

    const removeSelected = useCallback(() => {
        if (!draft || !selection || locked) return;
        change(selection === "roi" ? { ...draft, roi: null } : removeObject(draft, selection), null);
    }, [draft, selection, locked, change]);

    const latest = useRef({ save, dirty, busy, removeSelected, navigation, zoom });
    latest.current = { save, dirty, busy, removeSelected, navigation, zoom };

    useEffect(() => {
        const keydown = (event: KeyboardEvent) => {
            if (overlayOpen()) return;
            const current = latest.current;
            const control = event.ctrlKey || event.metaKey;
            const key = event.key.toLowerCase();
            if (control && (key === "s" || key === "ы")) {
                event.preventDefault();
                if (current.dirty && !current.busy) void current.save().catch(() => {});
                return;
            }
            if (isTypingTarget(event.target)) return;
            if (control && (key === "z" || key === "я") && !event.shiftKey) {
                event.preventDefault();
                setHistory(value => value && undo(value));
                setSelection(null);
            } else if (control && (key === "y" || key === "н" || ((key === "z" || key === "я") && event.shiftKey))) {
                event.preventDefault();
                setHistory(value => value && redo(value));
                setSelection(null);
            } else if (event.key === "Delete" || event.key === "Backspace") {
                event.preventDefault();
                current.removeSelected();
            } else if (event.key === "Escape") {
                setSelection(null);
            } else if (event.key === "ArrowLeft" && !control && current.navigation.previous) {
                event.preventDefault();
                current.navigation.previous();
            } else if (event.key === "ArrowRight" && !control && current.navigation.next) {
                event.preventDefault();
                current.navigation.next();
            } else current.zoom.handleZoomKey(event);
        };
        window.addEventListener("keydown", keydown);
        return () => window.removeEventListener("keydown", keydown);
    }, []);

    function chooseClass(value: string) {
        setClassId(value);
        setClassVariantId(null);
        if (selectedObject && draft && value !== selectedObject.classId) change(updateObject(draft, selectedObject.id, { classId: value, classVariantId: null }));
    }

    function chooseVariant(value: string) {
        const next = value === noVariant ? null : value;
        setClassVariantId(next);
        if (selectedObject && draft && next !== selectedObject.classVariantId) change(updateObject(draft, selectedObject.id, { classVariantId: next }));
    }

    function renew() {
        imagesApi.get({ projectId, imageId: image.id }).then(setSource, () => {});
    }

    const classes = palette.activeClasses;
    const classItems = selectedObject && !classes.some(item => item.id === selectedObject.classId)
        ? [...classes, { id: selectedObject.classId, name: `${palette.labelOf(selectedObject.classId, null)} (в архиве)` }]
        : classes;
    const variants = palette.bindingsOf(effectiveClassId);
    const variantItems = effectiveVariantId && !variants.some(item => item.binding.id === effectiveVariantId)
        ? [...variants.map(item => ({ id: item.binding.id, name: item.name, color: item.color })),
            { id: effectiveVariantId, name: `${palette.labelOf(effectiveClassId, effectiveVariantId)} (в архиве)`, color: palette.colorOf(effectiveClassId, effectiveVariantId) }]
        : variants.map(item => ({ id: item.binding.id, name: item.name, color: item.color }));
    const status = dirty ? "Есть несохранённые изменения" : stored?.reviewState === "reviewed" ? "Проверено" : "Не проверено";
    const index = selectedObject && draft ? draft.objects.indexOf(selectedObject) + 1 : 0;

    return (
        <section className="space-y-3 rounded-md border bg-card p-3" aria-label={`Разметка ${image.displayName}`} data-testid="annotation-editor" data-dirty={dirty || undefined}>
            <div className="flex flex-wrap items-center gap-2">
                <Button size="sm" variant="outline" onClick={() => inspect(draft, stored?.revision)}>Просмотр и масштаб</Button>
                <Button size="sm" variant="outline" data-testid="editor-back" onClick={close}>Назад к изображениям</Button>
                <Button size="icon-sm" variant="ghost" aria-label="Предыдущее изображение" title="Предыдущее изображение (←)" disabled={!navigation.previous} onClick={navigation.previous}><ChevronLeft /></Button>
                <Button size="icon-sm" variant="ghost" aria-label="Следующее изображение" title="Следующее изображение (→)" disabled={!navigation.next} onClick={navigation.next}><ChevronRight /></Button>
                <div className="flex min-w-0 flex-1 flex-wrap items-center justify-between gap-2">
                    <h3 className="min-w-0 font-medium break-words">Разметка · {image.displayName}</h3>
                    <span className={`text-xs ${dirty ? "text-amber-300" : "text-muted-foreground"}`} data-testid="editor-status" role="status">{status}</span>
                </div>
            </div>
            {lockReason && <p className="text-sm text-muted-foreground" data-testid="editor-locked">{lockReason}</p>}
            {loadError && (
                <Alert variant="destructive">
                    <AlertDescription>
                        <p>{loadError}</p>
                        <Button variant="ghost" size="sm" className="mt-1" onClick={() => setAttempt(value => value + 1)}>Повторить</Button>
                    </AlertDescription>
                </Alert>
            )}
            <div className="flex flex-wrap items-center gap-2">
                <Select value={tool} onValueChange={value => { setTool(value as AnnotationTool); setSelection(null); }} disabled={locked}>
                    <SelectTrigger size="sm" aria-label="Инструмент разметки" className="w-36" data-testid="editor-tool"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        <SelectItem value="object">Объект</SelectItem>
                        <SelectItem value="roi">Область ROI</SelectItem>
                    </SelectContent>
                </Select>
                <Select value={effectiveClassId} onValueChange={chooseClass} disabled={locked}>
                    <SelectTrigger size="sm" aria-label="Класс объекта" className="w-44" data-testid="editor-class"><SelectValue placeholder="Выберите класс" /></SelectTrigger>
                    <SelectContent>
                        {classItems.map(item => <SelectItem key={item.id} value={item.id}>{item.name}</SelectItem>)}
                    </SelectContent>
                </Select>
                <Select value={effectiveVariantId ?? noVariant} onValueChange={chooseVariant} disabled={locked || !effectiveClassId}>
                    <SelectTrigger size="sm" aria-label="Вариант объекта" className="w-44" data-testid="editor-variant"><SelectValue /></SelectTrigger>
                    <SelectContent>
                        <SelectItem value={noVariant}>Без варианта</SelectItem>
                        {variantItems.map(item => (
                            <SelectItem key={item.id} value={item.id}>
                                <span className="flex items-center gap-2">
                                    <span className="size-2.5 shrink-0 rounded-sm" style={{ backgroundColor: item.color }} />
                                    {item.name}
                                </span>
                            </SelectItem>
                        ))}
                    </SelectContent>
                </Select>
                {binding ? (
                    <VariantColorInput
                        key={binding.id}
                        name={palette.labelOf(binding.classId, binding.id)}
                        color={binding.color}
                        displayColor={binding.displayColor}
                        disabled={!binding.available}
                        label="Цвет рамок варианта"
                        resetText="Цвет по умолчанию"
                        save={color => setColor(binding, color)}
                    />
                ) : (
                    <input
                        type="color"
                        aria-label="Цвет рамок варианта"
                        title="Выберите вариант, чтобы задать цвет. Рамки без варианта окрашены по классу."
                        disabled
                        value={palette.colorOf(effectiveClassId, null)}
                        className="h-8 w-10 rounded-md border bg-transparent p-1 disabled:cursor-not-allowed disabled:opacity-50"
                    />
                )}
            </div>
            <div className="flex flex-wrap items-center gap-2">
                <ZoomControls zoom={zoom} />
                <div className="ml-auto flex flex-wrap items-center gap-2">
                    <Button size="sm" variant="outline" aria-label="Отменить (Ctrl+Z)" title="Отменить (Ctrl+Z)" data-testid="editor-undo" disabled={locked || !history || !canUndo(history)}
                        onClick={() => { setHistory(value => value && undo(value)); setSelection(null); }}><Undo2 />Отменить</Button>
                    <Button size="sm" variant="outline" aria-label="Повторить (Ctrl+Y)" title="Повторить (Ctrl+Y)" data-testid="editor-redo" disabled={locked || !history || !canRedo(history)}
                        onClick={() => { setHistory(value => value && redo(value)); setSelection(null); }}><Redo2 />Повторить</Button>
                </div>
            </div>
            <AnnotationCanvas
                image={source}
                draft={draft}
                selection={selection}
                tool={tool}
                classId={classId}
                classVariantId={classVariantId}
                palette={palette}
                locked={locked || busy}
                zoom={zoom}
                select={setSelection}
                commit={change}
                renew={renew}
            />
            {selectedObject && <p className="text-xs text-muted-foreground" data-testid="editor-selection">Выбрана рамка {index}: класс и вариант применяются к ней. Delete удаляет рамку.</p>}
            <p className="text-xs text-muted-foreground">
                Протяните рамку на изображении. ROI сохраняет отмеченный сектор кадра. Датасет копирует весь готовый кадр без обрезки. Цвет рамки соответствует варианту — его можно сменить рядом с выбором варианта.
                {" "}Рамку можно выбрать, перетащить и изменить за маркеры; Shift рисует новую поверх. Ctrl+колесо — масштаб, пробел или средняя кнопка — перемещение кадра, Ctrl+Z / Ctrl+Y — отмена и повтор, Ctrl+S — сохранить.
            </p>
            <ImageDetails image={image} annotation={draft} revision={stored?.revision} palette={palette} />
            {draft && (
                <>
                    <div className="flex flex-wrap gap-2">
                        {draft.objects.map((item, position) => (
                            <Button
                                key={item.id}
                                variant="outline"
                                size="sm"
                                title={palette.labelOf(item.classId, item.classVariantId)}
                                disabled={locked}
                                aria-pressed={selection === item.id}
                                onClick={() => change(removeObject(draft, item.id), selection === item.id ? null : selection)}
                            >
                                <span className="size-2.5 shrink-0 rounded-sm" style={{ backgroundColor: palette.colorOf(item.classId, item.classVariantId) }} />
                                Удалить рамку {position + 1}
                                <span className="max-w-48 truncate text-muted-foreground">{palette.labelOf(item.classId, item.classVariantId)}</span>
                            </Button>
                        ))}
                        {draft.roi && (
                            <Button variant="outline" size="sm" disabled={locked} onClick={() => change({ ...draft, roi: null }, selection === "roi" ? null : selection)}>Убрать ROI</Button>
                        )}
                    </div>
                    <div className="flex flex-wrap items-center gap-4">
                        <label className="flex items-center gap-2 text-sm">
                            <Checkbox
                                disabled={locked}
                                data-testid="editor-reviewed"
                                checked={draft.reviewState === "reviewed"}
                                onCheckedChange={checked => change({ ...draft, reviewState: checked === true ? "reviewed" : "unreviewed" })}
                            />
                            Проверено, включая пустой кадр
                        </label>
                        <Button data-testid="annotation-save" disabled={locked || busy || !dirty} onClick={() => void save().catch(() => {})}>
                            {busy ? "Сохранение…" : "Сохранить разметку"}
                        </Button>
                    </div>
                </>
            )}
            <div aria-live="polite">{error && <p role="alert" className="text-sm text-destructive" data-testid="editor-error">{error}</p>}</div>
        </section>
    );
}
